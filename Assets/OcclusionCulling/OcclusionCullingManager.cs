using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using static UnityEngine.ObjectDispatcher;

public class OcclusionCullingManager : MonoBehaviour
{
    ObjectDispatcher m_Dispatcher;

    void Awake()
    {
        m_Dispatcher = new ObjectDispatcher();
        m_Dispatcher.EnableTransformTracking<MeshRenderer>(TransformTrackingType.GlobalTRS);

        m_Dispatcher = new ObjectDispatcher();
        m_Dispatcher.EnableTypeTracking<LODGroup>(TypeTrackingFlags.SceneObjects);
        m_Dispatcher.EnableTypeTracking<Mesh>();
        m_Dispatcher.EnableTypeTracking<Material>();
        m_Dispatcher.EnableTransformTracking<LODGroup>(TransformTrackingType.GlobalTRS);
        m_Dispatcher.EnableTypeTracking<MeshRenderer>(TypeTrackingFlags.SceneObjects);
        m_Dispatcher.EnableTransformTracking<MeshRenderer>(TransformTrackingType.GlobalTRS);

        m_GPUDrivenProcessor = new GPUDrivenProcessor();

        var renderers = GameObject.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        m_GPUDrivenProcessor.EnableGPUDrivenRenderingAndDispatchRendererData(
            renderersID: renderers.Select(r => r.GetInstanceID()).ToArray(),
            callback: (in GPUDrivenRendererGroupData rendererData, IList<Mesh> meshes, IList<Material> materials) =>
            {
                Debug.Log($"GPUDrivenRendererDataCallback called with {rendererData.instancesCount.Length} renderers, {meshes.Count} meshes, and {materials.Count} materials.");
            },
            materialUpdateOnly: false);
    }

    GPUDrivenProcessor m_GPUDrivenProcessor;

    void LateUpdate()
    {
        // var changes = m_Dispatcher.GetTransformChangesAndClear<MeshRenderer>(TransformTrackingType.GlobalTRS);
        // if (changes.Length > 0)
        // {
        //     var list = string.Join("\n", changes.Select(c => c.name));
        //     Debug.Log($"Transform changes count:{changes.Length}\n{list}");
        // }

        var lodGroupTransformData = m_Dispatcher.GetTransformChangesAndClear<LODGroup>(TransformTrackingType.GlobalTRS, Allocator.TempJob);
        var meshTransformData = m_Dispatcher.GetTransformChangesAndClear<MeshRenderer>(TransformTrackingType.GlobalTRS, Allocator.TempJob);
        var lodGroupData = m_Dispatcher.GetTypeChangesAndClear<LODGroup>(Allocator.TempJob, noScriptingArray: true);
        var meshDataSorted = m_Dispatcher.GetTypeChangesAndClear<Mesh>(Allocator.TempJob, sortByInstanceID: true, noScriptingArray: true);
        var materialData = m_Dispatcher.GetTypeChangesAndClear<Material>(Allocator.TempJob, noScriptingArray: true);
        var rendererData = m_Dispatcher.GetTypeChangesAndClear<MeshRenderer>(Allocator.TempJob, noScriptingArray: true);

        if (lodGroupTransformData.positions.Length > 0)
        {
            Debug.Log($"LODGroup transform changes count: {lodGroupTransformData.positions.Length}");
        }
        if (meshTransformData.positions.Length > 0
            || meshTransformData.rotations.Length > 0
            || meshTransformData.scales.Length > 0
            || meshTransformData.localToWorldMatrices.Length > 0
            || meshTransformData.parentID.Length > 0
            || meshTransformData.transformedID.Length > 0)
        {
            Debug.Log($"MeshRenderer transform changes count: {meshTransformData.positions.Length}");

            // m_GPUDrivenProcessor.EnableGPUDrivenRenderingAndDispatchRendererData(
            //     rendererData: meshTransformData.
            // )
        }
        if (lodGroupData.changed != null)
        {
            Debug.Log($"LODGroup data changes count: {lodGroupData.changed.Length}");
        }
        if (meshDataSorted.changed != null)
        {
            Debug.Log($"Mesh data changes count: {meshDataSorted.changed.Length}");
        }
        if (materialData.changed != null)
        {
            Debug.Log($"Material data changes count: {materialData.changed.Length}");
        }
        if (rendererData.changed != null)
        {
            Debug.Log($"MeshRenderer data changes count: {rendererData.changed.Length}");
        }

    }
}
