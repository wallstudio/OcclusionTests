using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

public class SimpleGpuDrivenRendererGroup : MonoBehaviour
{
    GPUDrivenProcessor m_GPUDrivenProcessor;
    BatchRendererGroup m_BRG;
    Dictionary<(Mesh mesh, Material material), (MeshRenderer[] renderers, GraphicsBuffer buffer, BatchID bid)> m_OriginalRenderers;
    Dictionary<Mesh, BatchMeshID> m_Meshes;
    Dictionary<Material, BatchMaterialID> m_Materials;

    void Awake()
    {
        m_GPUDrivenProcessor = new GPUDrivenProcessor();
    }

    void OnDestroy()
    {
        m_GPUDrivenProcessor.Dispose();
    }

    void OnEnable()
    {
        m_OriginalRenderers = GetComponentsInChildren<MeshRenderer>()
            .GroupBy(r => (r.GetComponent<MeshFilter>().sharedMesh, r.sharedMaterial))
            .ToDictionary(g => g.Key, g => (g.ToArray(), default(GraphicsBuffer), default(BatchID)));

        m_GPUDrivenProcessor.EnableGPUDrivenRenderingAndDispatchRendererData(
            renderersID: m_OriginalRenderers.SelectMany(r => r.Value.renderers).Select(r => r.GetInstanceID()).ToArray(),
            callback: GPUDrivenRendererDataCallback,
            materialUpdateOnly: false);

        void GPUDrivenRendererDataCallback(in GPUDrivenRendererGroupData rendererData, IList<Mesh> meshes, IList<Material> materials)
        {
        }

        m_BRG = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
        m_Meshes = m_OriginalRenderers.Keys.Select(k => k.mesh).Distinct().ToDictionary(m => m, m => m_BRG.RegisterMesh(m));
        m_Materials = m_OriginalRenderers.Keys.Select(k => k.material).Distinct().ToDictionary(m => m, m => m_BRG.RegisterMaterial(m));

        var writer = new ArrayBufferWriter<byte>();
        foreach (var (state, (renderers, _, _)) in m_OriginalRenderers.ToArray())
        {
            writer.Clear();
            writer.GetSpan(64).Fill(0); // 最初の64バイトは0埋めが必要
            writer.Advance(64);

            var baseAddrO2W = (uint)writer.WrittenCount;
            var o2wSpan = MemoryMarshal.Cast<byte, PackedMatrix>(writer.GetSpan(UnsafeUtility.SizeOf<PackedMatrix>() * renderers.Length));
            for (int i = 0; i < renderers.Length; i++)
            {
                var v = renderers[i].transform.localToWorldMatrix;
                o2wSpan[i] = new PackedMatrix(v);
            }
            writer.Advance(UnsafeUtility.SizeOf<PackedMatrix>() * renderers.Length);

            var baseAddrW2O = (uint)writer.WrittenCount;
            var w2oSpan = MemoryMarshal.Cast<byte, PackedMatrix>(writer.GetSpan(UnsafeUtility.SizeOf<PackedMatrix>() * renderers.Length));
            for (int i = 0; i < renderers.Length; i++)
            {
                var v = renderers[i].transform.worldToLocalMatrix;
                w2oSpan[i] = new PackedMatrix(v);
            }
            writer.Advance(UnsafeUtility.SizeOf<PackedMatrix>() * renderers.Length);

            var baseAddrColor = (uint)writer.WrittenCount;
            var colorSpan = MemoryMarshal.Cast<byte, float4>(writer.GetSpan(UnsafeUtility.SizeOf<float4>() * renderers.Length));
            for (int i = 0; i < renderers.Length; i++)
            {
                var c = renderers[i].sharedMaterial.GetColor("_BaseColor");
                colorSpan[i] = new float4(c.r, c.g, 7.7f, c.a);
            }
            writer.Advance(UnsafeUtility.SizeOf<float4>() * renderers.Length);

            var bufferSize = Mathf.FloorToInt(writer.WrittenCount / 4.0f) * 4; // 4の倍数に
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bufferSize / 4, 4); // 確保が4Byte単位なので
            buffer.SetData(writer.WrittenMemory.ToArray());
            buffer.name = $"GPUDrivenRendererGroupBuffer_{state.mesh.name}_{state.material.name}";

            var overrideBit = 0b_10000000_00000000_00000000_00000000; // 0x80000000
            var batchId = m_BRG.AddBatch(
                batchMetadata: new NativeArray<MetadataValue>(3, Allocator.Temp)
                {
                    [0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = overrideBit | baseAddrO2W, },
                    [1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = overrideBit | baseAddrW2O, },
                    [2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"), Value = overrideBit | baseAddrColor, },
                },
                buffer: buffer.bufferHandle);

            m_OriginalRenderers[state] = (renderers, buffer, batchId);
        }
    }

    unsafe JobHandle OnPerformCulling(
        BatchRendererGroup rendererGroup, BatchCullingContext cullingContext,
        BatchCullingOutput cullingOutput, IntPtr userContext)
    {
        ref var cmd = ref cullingOutput.drawCommands.AsSpan()[0];

        cmd.drawCommandCount = m_OriginalRenderers.Count;
        cmd.drawCommands = Malloc<BatchDrawCommand>(cmd.drawCommandCount);

        cmd.visibleInstanceCount = m_OriginalRenderers.Sum(kv => kv.Value.renderers.Length);
        cmd.visibleInstances = Malloc<int>(cmd.visibleInstanceCount);

        var cmdIdxOfBatch = 0;
        var vInsIdxOfBatch = 0;
        foreach (var ((mesh, material), (renderers, _, batchId)) in m_OriginalRenderers)
        {
            cmd.drawCommands[cmdIdxOfBatch] = new BatchDrawCommand
            {
                visibleOffset = (uint)vInsIdxOfBatch,
                visibleCount = (uint)renderers.Length,
                batchID = batchId,
                meshID = m_Meshes[mesh],
                materialID = m_Materials[material],
                submeshIndex = 0,
                splitVisibilityMask = 0xff,
                flags = BatchDrawCommandFlags.None,
                sortingPosition = 0,
            };
            var s = new Span<int>(cmd.visibleInstances, 4);
            for (int i = 0; i < renderers.Length; i++)
                cmd.visibleInstances[vInsIdxOfBatch + i] = i;

            vInsIdxOfBatch += renderers.Length;
            cmdIdxOfBatch += 1;
        }

        cmd.drawRangeCount = 1;
        cmd.drawRanges = Malloc<BatchDrawRange>(1);
        cmd.drawRanges[0] = new BatchDrawRange
        {
            drawCommandsBegin = 0,
            drawCommandsCount = (uint)cmd.drawCommandCount,
            filterSettings = new BatchFilterSettings
            {
                renderingLayerMask = 0b00000000_00000000_00000000_00000001,
                // layer = 0b00000000_00000000_00000000_00000000, // default
                // motionMode = MotionVectorGenerationMode.Camera,
                // shadowCastingMode = ShadowCastingMode.On,
                // receiveShadows = true,
                // staticShadowCaster = false,
                // allDepthSorted = false,
            },
        };

        cmd.drawCommandPickingInstanceIDs = null;
        cmd.instanceSortingPositions = null;
        cmd.instanceSortingPositionFloatCount = 0;

        return new JobHandle();
    }

    void OnDisable()
    {
        m_GPUDrivenProcessor.DisableGPUDrivenRendering(
            renderersID: m_OriginalRenderers.SelectMany(r => r.Value.renderers).Select(r => r.GetInstanceID()).ToArray());
        m_BRG.Dispose();
        m_BRG = null;
    }

    static unsafe T* Malloc<T>(int count) where T : unmanaged
    {
        var size = UnsafeUtility.SizeOf<T>() * count;
        var alignment = UnsafeUtility.AlignOf<T>();
        return (T*)UnsafeUtility.Malloc(size, alignment, Allocator.TempJob);
    }

    struct PackedMatrix
    {
        public float c0x, c0y, c0z, c1x, c1y, c1z, c2x, c2y, c2z, c3x, c3y, c3z;
        public PackedMatrix(Matrix4x4 m)
        {
            c0x = m.m00; c0y = m.m10; c0z = m.m20;
            c1x = m.m01; c1y = m.m11; c1z = m.m21;
            c2x = m.m02; c2y = m.m12; c2z = m.m22;
            c3x = m.m03; c3y = m.m13; c3z = m.m23;
        }
    }
}
