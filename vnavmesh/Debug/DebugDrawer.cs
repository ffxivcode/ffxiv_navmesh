﻿using Dalamud.Interface.Utility;
using Dalamud.Utility;
using ImGuiNET;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Debug;

public unsafe class DebugDrawer : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();
    public RenderTarget? RenderTarget { get; private set; }
    public EffectMesh EffectMesh { get; init; }
    public EffectBox EffectBox { get; init; }

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Matrix Proj { get; private set; }
    public SharpDX.Matrix View { get; private set; }
    public SharpDX.Matrix CameraWorld { get; private set; }
    public float CameraAzimuth { get; private set; } // facing north = 0, facing west = pi/4, facing south = +-pi/2, facing east = -pi/4
    public float CameraAltitude { get; private set; } // facing horizontally = 0, facing down = pi/4, facing up = -pi/4
    public SharpDX.Vector2 ViewportSize { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetEngineCoreSingletonDelegate();

    private nint _engineCoreSingleton;
    private EffectMesh.Data _meshDynamicData;
    private EffectMesh.Data.Builder? _meshDynamicBuilder;
    private EffectBox.Data _boxDynamicData;
    private EffectBox.Data.Builder? _boxDynamicBuilder;

    private List<(Vector2 from, Vector2 to, uint col)> _viewportLines = new();

    public DebugDrawer()
    {
        EffectMesh = new(RenderContext);
        EffectBox = new(RenderContext);

        _engineCoreSingleton = Marshal.GetDelegateForFunctionPointer<GetEngineCoreSingletonDelegate>(Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? 48 89 4C 24 ?? 4C 8D 4D ?? 4C 8D 44 24 ??"))();
        _meshDynamicData = new(RenderContext, 16 * 1024 * 1024, 16 * 1024 * 1024, 128 * 1024, true);
        _boxDynamicData = new(RenderContext, 16 * 1024 * 1024, true);
    }

    public void Dispose()
    {
        _boxDynamicBuilder?.Dispose();
        _boxDynamicData.Dispose();
        _meshDynamicBuilder?.Dispose();
        _meshDynamicData.Dispose();
        EffectBox.Dispose();
        EffectMesh.Dispose();
        RenderTarget?.Dispose();
        RenderContext.Dispose();
    }

    public void StartFrame()
    {
        ViewProj = ReadMatrix(_engineCoreSingleton + 0x1B4);
        Proj = ReadMatrix(_engineCoreSingleton + 0x174);
        View = ViewProj * SharpDX.Matrix.Invert(Proj);
        CameraWorld = SharpDX.Matrix.Invert(View);
        CameraAzimuth = MathF.Atan2(View.Column3.X, View.Column3.Z);
        CameraAltitude = MathF.Asin(View.Column3.Y);
        ViewportSize = ReadVec2(_engineCoreSingleton + 0x1F4);

        EffectBox.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        EffectMesh.UpdateConstants(RenderContext, new() { ViewProj = ViewProj, LightingWorldYThreshold = 45.Degrees().Cos() });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y);
        }
        RenderTarget.Bind(RenderContext);
    }

    public void EndFrame()
    {
        if (_boxDynamicBuilder != null)
        {
            _boxDynamicBuilder.Dispose();
            _boxDynamicBuilder = null;
            EffectBox.Draw(RenderContext, _boxDynamicData);
        }

        if (_meshDynamicBuilder != null)
        {
            _meshDynamicBuilder.Dispose();
            _meshDynamicBuilder = null;
            EffectMesh.Draw(RenderContext, _meshDynamicData);
        }

        RenderContext.Execute();

        //if (_viewportLines.Count == 0)
        //    return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(0, 0));
        ImGui.Begin("world_overlay", ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        var dl = ImGui.GetWindowDrawList();
        foreach (var l in _viewportLines)
            dl.AddLine(l.from, l.to, l.col);
        _viewportLines.Clear();

        if (RenderTarget != null)
        {
            ImGui.GetWindowDrawList().AddImage(RenderTarget.ImguiHandle, new(), new(RenderTarget.Size.X, RenderTarget.Size.Y));
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    public void DrawMesh(IMesh mesh, ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world, Vector4 color) => GetDynamicMeshes().Add(mesh, ref world, color);
    public void DrawMeshes(IMesh mesh, IEnumerable<EffectMesh.Instance> instances) => GetDynamicMeshes().Add(mesh, instances);

    public void DrawBox(ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world, Vector4 colorTop, Vector4 colorSide) => GetDynamicBoxes().Add(ref world, colorTop, colorSide);
    public void DrawBox(ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world, Vector4 color) => GetDynamicBoxes().Add(ref world, color, color);

    public void DrawAABB(Vector3 min, Vector3 max, Vector4 colorTop, Vector4 colorSide)
    {
        var center = (max + min) * 0.5f;
        var extent = (max - min) * 0.5f;
        FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 m = new() { M11 = extent.X, M22 = extent.Y, M33 = extent.Z, M41 = center.X, M42 = center.Y, M43 = center.Z };
        DrawBox(ref m, colorTop, colorSide);
    }
    public void DrawAABB(Vector3 min, Vector3 max, Vector4 color) => DrawAABB(min, max, color, color);

    public void DrawWorldLine(Vector3 start, Vector3 end, uint color)
    {
        var p1 = start.ToSharpDX();
        var p2 = end.ToSharpDX();
        if (ClipLineToNearPlane(ref p1, ref p2))
            _viewportLines.Add((WorldToScreen(p1), WorldToScreen(p2), color));
    }

    public void DrawWorldSphere(Vector3 center, float radius, uint color)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(radius, 360.Degrees(), 0.1f);
        var prev1 = center + new Vector3(0, 0, radius);
        var prev2 = center + new Vector3(0, radius, 0);
        var prev3 = center + new Vector3(radius, 0, 0);
        for (int i = 1; i <= numSegments; ++i)
        {
            var dir = (i * 360.0f / numSegments).Degrees().ToDirection();
            var curr1 = center + radius * new Vector3(dir.X, 0, dir.Y);
            var curr2 = center + radius * new Vector3(0, dir.Y, dir.X);
            var curr3 = center + radius * new Vector3(dir.Y, dir.X, 0);
            DrawWorldLine(curr1, prev1, color);
            DrawWorldLine(curr2, prev2, color);
            DrawWorldLine(curr3, prev3, color);
            prev1 = curr1;
            prev2 = curr2;
            prev3 = curr3;
        }
    }

    public void DrawWorldTriangle(Vector3 v1, Vector3 v2, Vector3 v3, uint color)
    {
        DrawWorldLine(v1, v2, color);
        DrawWorldLine(v2, v3, color);
        DrawWorldLine(v3, v1, color);
    }

    public void DrawWorldPoint(Vector3 p, uint color)
    {
        var pw = p.ToSharpDX();
        var nearPlane = ViewProj.Column3;
        if (SharpDX.Vector4.Dot(new(pw, 1), nearPlane) <= 0)
            return;

        var ps = WorldToScreen(pw);
        foreach (var (from, to) in AdjacentPairs(CurveApprox.Circle(ps, 5, 1)))
            _viewportLines.Add((from, to, color));
    }

    private EffectMesh.Data.Builder GetDynamicMeshes() => _meshDynamicBuilder ??= _meshDynamicData.Map(RenderContext);
    private EffectBox.Data.Builder GetDynamicBoxes() => _boxDynamicBuilder ??= _boxDynamicData.Map(RenderContext);

    private unsafe SharpDX.Matrix ReadMatrix(nint address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }

    private unsafe SharpDX.Vector2 ReadVec2(nint address)
    {
        var p = (float*)address;
        return new(p[0], p[1]);
    }

    private bool ClipLineToNearPlane(ref SharpDX.Vector3 a, ref SharpDX.Vector3 b)
    {
        var n = ViewProj.Column3; // near plane
        var an = SharpDX.Vector4.Dot(new(a, 1), n);
        var bn = SharpDX.Vector4.Dot(new(b, 1), n);
        if (an <= 0 && bn <= 0)
            return false;

        if (an < 0 || bn < 0)
        {
            var ab = b - a;
            var abn = SharpDX.Vector3.Dot(ab, new(n.X, n.Y, n.Z));
            var t = -an / abn;
            if (an < 0)
                a = a + t * ab;
            else
                b = a + t * ab;
        }
        return true;
    }

    private Vector2 WorldToScreen(SharpDX.Vector3 w)
    {
        var p = SharpDX.Vector3.TransformCoordinate(w, ViewProj);
        return new Vector2(0.5f * ViewportSize.X * (1 + p.X), 0.5f * ViewportSize.Y * (1 - p.Y)) + ImGuiHelpers.MainViewport.Pos;
    }

    private static IEnumerable<(Vector2, Vector2)> AdjacentPairs(IEnumerable<Vector2> v)
    {
        var en = v.GetEnumerator();
        if (!en.MoveNext())
            yield break;
        var first = en.Current;
        var from = en.Current;
        while (en.MoveNext())
        {
            yield return (from, en.Current);
            from = en.Current;
        }
        yield return (from, first);
    }
}