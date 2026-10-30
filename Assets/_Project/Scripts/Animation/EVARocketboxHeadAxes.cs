using UnityEngine;

// Calibrated in the imported model's rest pose; remains valid across appearance
// switches, Animator.Rebind and all three conversation animation styles.
[DisallowMultipleComponent]
public sealed class EVARocketboxHeadAxes : MonoBehaviour
{
    [HideInInspector] public Vector3 forward;
    [HideInInspector] public Vector3 up;
}
