using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using UnityEngine.AI;

public class NPCSensorScript : MonoBehaviour
{
    [HideInInspector] public float lastScore = 0f;
    [HideInInspector] public bool canMove = true;

    [Header("Audio")]
    public NPCAudioCapture audioCapture;

    [Header("Camera")]
    public Camera npcCamera;
    public RenderTexture renderTexture;

    [Header("Server")]
    public string serverUrl = "http://127.0.0.1:8000/analyze";
    public float sendInterval = 2.0f;
    public float maxMoveDistance = 3.0f;
    public NavMeshAgent agent;

    private Vector3 lastDestination = Vector3.zero;

    public Quaternion capturedRotation;
    private Vector3 capturedPosition;
    private Vector3 capturedTargetPosition;
    public bool targetWasInView = false;
    private bool capturedTargetInView;


    void Start()
    {
        StartCoroutine(CaptureAndSend());
    }

    IEnumerator CaptureAndSend()
    {
        while (true)
        {
            yield return new WaitForSeconds(sendInterval);

            capturedRotation = npcCamera.transform.rotation;
            capturedPosition = npcCamera.transform.position;
            NPCStateMachine fsm = GetComponent<NPCStateMachine>();
            if (fsm != null && fsm.target != null)
            {
                capturedTargetPosition = fsm.target.transform.position;
                TargetVisibility vis = fsm.target.GetComponentInChildren<TargetVisibility>();
                targetWasInView = vis != null && vis.WasRecentlyVisible();
                Debug.Log($"[Capture] targetWasInView:{targetWasInView} lastSeenFrame:{(vis != null ? vis.ToString() : "null")} currentFrame:{Time.frameCount}");
            }
            // image capture
            RenderTexture.active = renderTexture;
            Texture2D tex = new Texture2D(256, 256, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            tex.Apply();
            byte[] imgBytes = tex.EncodeToJPG(75);
            RenderTexture.active = null;
            Destroy(tex);
            // audio capture
            audioCapture.StartCapture();
            yield return new WaitForSeconds(1.0f);
            byte[] wavBytes = audioCapture.StopAndGetWAV();
            if (wavBytes == null) continue;

            float amp = audioCapture.GetLastAmplitude();
            Debug.Log($"Audio amplitude: {amp}");
            if (amp < 0.02f)
            {
                Debug.Log("No sound detected, skipping");
                continue;
            }

            yield return SendToServer(imgBytes, wavBytes);
            Debug.Log($"Sent — image: {imgBytes.Length} bytes, audio: {wavBytes.Length} bytes");
        }
    }

    IEnumerator SendToServer(byte[] img, byte[] wav)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("image", img, "frame.jpg", "image/jpeg");
        form.AddBinaryData("audio", wav, "audio.wav", "audio/wav");

        using var req = UnityWebRequest.Post(serverUrl, form);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            ApplyCandidates(req.downloadHandler.text);
        else
            Debug.LogError("Server error: " + req.error);
    }

    void ApplyCandidates(string json)
    {
        NPCStateMachine fsm = GetComponent<NPCStateMachine>();
        if (fsm == null || 
            (fsm.currentState != NPCState.Scan && 
            fsm.currentState != NPCState.LocalScan &&
            fsm.currentState != NPCState.FullScanAtSpot)) return;

        ResponseData response = JsonUtility.FromJson<ResponseData>(json);
        if (response.status != "Found!") return;
        if (response.candidates.Length == 0) return;

        Candidate best = response.candidates[0];
        lastScore = best.score;

        Vector3 viewportPos = new Vector3(best.x / 224f, 1f - best.y / 224f, 0f);
        
        // 현재 회전이 아니라 캡처 당시 회전 사용
        Ray ray = new Ray(capturedPosition, capturedRotation * Vector3.forward);

        // viewport 보정
        Vector3 right = capturedRotation * Vector3.right;
        Vector3 up = capturedRotation * Vector3.up;
        Vector3 forward = capturedRotation * Vector3.forward;

        float fov = npcCamera.fieldOfView * Mathf.Deg2Rad;
        float aspect = npcCamera.aspect;

        Vector3 dir = forward 
            + right * (best.x / 224f - 0.5f) * Mathf.Tan(fov / 2) * aspect * 2f
            + up * (0.5f - best.y / 224f) * Mathf.Tan(fov / 2) * 2f;

        ray = new Ray(capturedPosition, dir.normalized);

        if (Physics.Raycast(ray, out RaycastHit hit, 100f))
        {
            Debug.Log($"Raycast hit: {hit.point}");
            Vector3 direction = (hit.point - transform.position).normalized;
            Vector3 clampedTarget = transform.position + direction * maxMoveDistance;
            NavMeshHit navHit;
            if (NavMesh.SamplePosition(clampedTarget, out navHit, 2f, NavMesh.AllAreas))
                GetComponent<NPCStateMachine>()?.SetMoveDestination(navHit.position, best.score, capturedPosition, capturedTargetPosition, targetWasInView);
        }
        else
        {
            Debug.Log("Raycast missed — using captured forward direction");
            Vector3 capturedForward = capturedRotation * Vector3.forward;
            capturedForward.y = 0;
            Vector3 clampedTarget = capturedPosition + capturedForward.normalized * maxMoveDistance;
            NavMeshHit navHit;
            if (NavMesh.SamplePosition(clampedTarget, out navHit, 2f, NavMesh.AllAreas))
                GetComponent<NPCStateMachine>()?.SetMoveDestination(navHit.position, best.score, capturedPosition, capturedTargetPosition, targetWasInView);
        }
    }
}


[System.Serializable] public class Candidate { public int x, y; public float score; }
[System.Serializable] public class CandidateResponse { public Candidate[] candidates; }

[System.Serializable]
public class ResponseData 
{ 
    public Candidate[] candidates; 
    public bool person_detected;
    public string status;
}