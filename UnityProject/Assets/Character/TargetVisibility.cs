using UnityEngine;

public class TargetVisibility : MonoBehaviour
{
    private int lastSeenFrame = -1;

    void OnWillRenderObject()
    {
        if (Camera.current != null && Camera.current.CompareTag("NPCCam"))
        {
            lastSeenFrame = Time.frameCount;
        }
    }

    public bool WasRecentlyVisible()
    {
        return Time.frameCount - lastSeenFrame <= 2;
    }
}