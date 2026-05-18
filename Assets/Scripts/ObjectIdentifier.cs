using UnityEngine;

/// <summary>
/// Marks a prefab root with its ObjectType so other systems can
/// identify it without relying on the GameObject name or tag.
/// </summary>
public class ObjectIdentifier : MonoBehaviour
{
    [Tooltip("The type this object represents. Must be unique per prefab.")]
    public ObjectType objectType;
}
