using UnityEngine;

public class ObjectDrag : MonoBehaviour
{
    private Rigidbody selectedRb;
    private Camera cam;
    private float dragDistance;

    void Start()
    {
        cam = Camera.main;
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                if (hit.rigidbody != null)
                {
                    selectedRb = hit.rigidbody;
                    selectedRb.useGravity = false;
                    dragDistance = Vector3.Distance(cam.transform.position, hit.point);
                }
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (selectedRb != null)
            {
                selectedRb.useGravity = true;
                selectedRb = null;
            }
        }
    }

    void FixedUpdate()
    {
        if (selectedRb != null)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 targetPos = ray.GetPoint(dragDistance);

            selectedRb.MovePosition(targetPos);
        }
    }
}