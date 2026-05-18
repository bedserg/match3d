#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Registers the "Dragging" physics layer automatically when the Editor loads.
/// The layer is used to prevent non-dragged objects from being accepted by the
/// DropZone trigger while a different object physically overlaps it.
/// </summary>
[InitializeOnLoad]
internal static class DraggingLayerSetup
{
    private const string LayerName = "Dragging";

    static DraggingLayerSetup()
    {
        EnsureLayerExists();
    }

    private static void EnsureLayerExists()
    {
        if (LayerMask.NameToLayer(LayerName) != -1)
            return; // Already registered.

        SerializedObject tagManager =
            new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>(
                "ProjectSettings/TagManager.asset"));

        SerializedProperty layers = tagManager.FindProperty("layers");
        if (layers == null || !layers.isArray)
        {
            Debug.LogError("[DraggingLayerSetup] Could not read TagManager layers array.");
            return;
        }

        // Layers 0-5 are built-in and read-only.  Find the first empty slot from index 6.
        for (int i = 6; i < layers.arraySize; i++)
        {
            SerializedProperty element = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(element.stringValue))
            {
                element.stringValue = LayerName;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[DraggingLayerSetup] Registered '{LayerName}' at layer index {i}.");
                return;
            }
        }

        Debug.LogError("[DraggingLayerSetup] No empty layer slot found. " +
                       "Please add the 'Dragging' layer manually in Project Settings > Tags & Layers.");
    }
}
#endif
