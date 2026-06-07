using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using System.Collections;

public enum NPCState
{
    Scan,           // look around 180°, send to server
    LocalScan, 
    FullScanAtSpot,
    FaceDirection,  // turn toward destination before moving
    Move,           // move toward heatmap candidate
    Chase,          // target found, go to it
    Return,         // return to origin
    FaceReturn,     // turn toward origin before returning
    Idle            // arrived at target, stay
}

public class NPCStateMachine : MonoBehaviour
{
    [Header("References")]
    public NavMeshAgent agent;
    public NPCSensorScript sensor;
    public NPCAudioCapture audioCapture;

    [Header("Target")]
    public float chaseDistance = 7f;
    public float lostTargetDistance = 12f;

    [Header("Scan Settings")]
    public float scanAngle = 360f;
    public float rotateSpeed = 45f;

    [Header("Move Settings")]
    public float faceSpeed = 5f;
    public float faceAngleThreshold = 10f;

    [Header("Sound Threshold")]
    public float amplitudeThreshold = 0.03f;
    public float scoreThreshold = 0.85f;

    public NPCState currentState = NPCState.Scan;
    public float returnCooldownTime = 3f;
    public float localScanAngle = 90f; // scan ±45° from current facing
    private Vector3 patrolOrigin;
    public GameObject target;

    private float scannedAngle = 0f;
    private bool scanDone = false;
    private bool conditionMet = false;
    private Vector3 moveDestination;
    private bool hasDestination = false;
    private float scanWaitTimer = 0f;
    private bool scanReversed = false;
    private Quaternion scanStartRotation;
    private float bestScore = 0f;
    private Vector3 bestDestination = Vector3.zero;
    private int scanCount = 0;
    private bool justFound = false;
    private float returnCooldown = 0f;
    private bool hasSearched = false;
    private int moveCount = 0;

    private Vector3 capturedTargetPosition;
    private int correctMoveCount = 0;
    private bool lastMoveWasCorrect = false;
    private Vector3 lastCapturedTargetPos;
    private float distanceBeforeMove = -1f;
    


    void Start()
    {
        patrolOrigin = transform.position;
        
        // find closest target
        GameObject[] targets = GameObject.FindGameObjectsWithTag("Target");
        float minDist = float.MaxValue;
        foreach (var t in targets)
        {
            float d = Vector3.Distance(transform.position, t.transform.position);
            if (d < minDist) { minDist = d; target = t; }
        }
        
        agent.isStopped = true;
        EnterScan();
    }
    void Update()
    {
        if (currentState == NPCState.Return || currentState == NPCState.Idle)
        {
            switch (currentState)
            {
                case NPCState.Return: UpdateReturn(); break;
                case NPCState.Idle:   UpdateIdle(); break;
            }
            return;
        }

        returnCooldown -= Time.deltaTime;

        GameObject[] targets = GameObject.FindGameObjectsWithTag("Target");
        foreach (var t in targets)
        {
            float d = Vector3.Distance(transform.position, t.transform.position);
            if (d < chaseDistance &&
                returnCooldown <= 0f &&
                currentState != NPCState.Chase &&
                currentState != NPCState.Idle &&
                currentState != NPCState.Return &&
                currentState != NPCState.FaceReturn)
            {
                target = t;
                SwitchState(NPCState.Chase);
                return;
            }
        }

        // timeout check
        if (currentState != NPCState.Return && 
            currentState != NPCState.Idle &&
            currentState != NPCState.Chase)
        {
            trialTimer += Time.deltaTime;
            if (trialTimer > trialTimeout)
            {
                Debug.Log($"[FSM] Trial timeout! Returning to origin.");
                trialTimer = 0f;
                currentTrial++;
                float timeoutTimeTaken = Time.time - epochStartTime;
                bool timeoutValid = hasSearched && scanCount > 1;
                StartCoroutine(LogEpochHTTP(currentTrial + 1, false, timeoutTimeTaken, scanCount, timeoutValid));
                scanCount = 0;
                moveCount = 0;
                correctMoveCount = 0; 
                distanceBeforeMove = -1f;
                epochStartTime = Time.time;
                SwitchState(NPCState.FaceReturn);
            }
        }

        switch (currentState)
        {
            case NPCState.Scan:          UpdateScan(); break;
            case NPCState.FaceDirection: UpdateFaceDirection(); break;
            case NPCState.Move:          UpdateMove(); break;
            case NPCState.Chase:         UpdateChase(); break;
            case NPCState.Return:        UpdateReturn(); break;
            case NPCState.FaceReturn:    UpdateFaceReturn(); break;
            case NPCState.Idle:          UpdateIdle(); break;
            case NPCState.LocalScan:     UpdateLocalScan(); break;
            case NPCState.FullScanAtSpot: UpdateFullScanAtSpot(); break;
        }
    }

    void EnterScan()
    {
        justFound = false;
        scanCount++;
        Debug.Log($"[FSM] Scan #{scanCount} started");
        agent.isStopped = true;
        agent.updateRotation = false;
        scannedAngle = 0f;
        scanDone = false;
        conditionMet = false;
        hasDestination = false;
        if (sensor != null) sensor.canMove = false;
        bestScore = 0f;
        bestDestination = Vector3.zero;
        lastCapturedTargetPos = Vector3.zero;

        Debug.Log("[FSM] Scan started");
        if (scanCount == 1)
        {
            epochStartTime = Time.time;
            trialTimer = 0f;
            hasSearched = false;
        }
    }


    void UpdateScan()
    {
        if (scanDone)
        {
            if (hasDestination)
                SwitchState(NPCState.FaceDirection);
            else
                SwitchState(NPCState.FaceReturn);
            return;
        }

        float step = rotateSpeed * Time.deltaTime;
        transform.Rotate(0, step, 0);
        scannedAngle += step;

        // 회전 중에도 조건 체크 → 찾으면 즉시 이동
        if (hasDestination) 
        {// move immediately when found
            scanDone = true;
            return;
        }

        if (scannedAngle >= scanAngle) // full rotation, nothing found
        {
            scanDone = true;
        }
    }

    void UpdateLocalScan()
    {
        if (scanDone)
        {
            if (hasDestination)
                SwitchState(NPCState.FaceDirection);
            else
                SwitchState(NPCState.FullScanAtSpot);
            return;
        }

        float step = rotateSpeed * Time.deltaTime;
        transform.Rotate(0, step, 0);
        scannedAngle += step;

        if (hasDestination)
        {
            scanDone = true;
            return;
        }

        if (scannedAngle >= localScanAngle)
            scanDone = true;
    }

    void UpdateFaceDirection()
    {
        if (!hasDestination) { SwitchState(NPCState.FaceReturn); return; }

        Vector3 dir = (moveDestination - transform.position).normalized;
        dir.y = 0;
        if (dir == Vector3.zero) { SwitchState(NPCState.Move); return; }

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, faceSpeed * Time.deltaTime);

        if (Quaternion.Angle(transform.rotation, targetRot) < faceAngleThreshold)
            SwitchState(NPCState.Move);
    }

    void UpdateMove()
    {
        if (!agent.pathPending && agent.remainingDistance < 0.5f)
        {
            Debug.Log("[FSM] Arrived at destination");

            // ★ 도착 시점에 거리 비교
            if (target != null && distanceBeforeMove > 0)
            {
                float distAfter = Vector3.Distance(transform.position, target.transform.position);
                bool closer = distAfter < distanceBeforeMove;
                if (closer) correctMoveCount++;
                Debug.Log($"[Move] distBefore:{distanceBeforeMove:F2} distAfter:{distAfter:F2} closer:{closer}");
                distanceBeforeMove = -1f;
            }

            if (target != null && Vector3.Distance(transform.position, target.transform.position) < chaseDistance)
            {
                SwitchState(NPCState.Chase);
                return;
            }
            SwitchState(NPCState.LocalScan);
        }
    }

    void UpdateChase()
    {
        if (target == null) { SwitchState(NPCState.Scan); return; }

        agent.isStopped = false;
        agent.SetDestination(target.transform.position);

        // contact = found
        if (Vector3.Distance(transform.position, target.transform.position) < 2f)
        {
            if (justFound) return; // prevent double count
            justFound = true;
            currentTrial++;

            float timeTaken = Time.time - epochStartTime;
            bool validFind = hasSearched && scanCount > 1;

            Debug.Log($"Found target! Trial {currentTrial}/{totalTrials}, valid:{validFind}");

            StartCoroutine(LogEpochHTTP(currentTrial, true, timeTaken, scanCount, validFind));

            if (currentTrial >= totalTrials)
            {
                agent.isStopped = true;
                Debug.Log($"All trials done. Total: {currentTrial}");
                currentState = NPCState.Idle;
                return;
            }
            else
            {
                scanCount = 0;
                moveCount = 0;
                correctMoveCount = 0;
                distanceBeforeMove = -1f;
                epochStartTime = Time.time;
                trialTimer = 0f;
                SwitchState(NPCState.Return);
            }

            return;
        }

        if (Vector3.Distance(transform.position, target.transform.position) > lostTargetDistance
            && (audioCapture == null || audioCapture.GetLastAmplitude() < amplitudeThreshold))
            SwitchState(NPCState.Scan);
    }
    private float idleTimer = 0f;
    public int totalTrials = 50;
    private int currentTrial = 0;
    private float epochStartTime = 0f;

    public float trialTimeout = 60f; // seconds per trial
    private float trialTimer = 0f;

    void UpdateIdle()
    {

    if (target != null && Vector3.Distance(transform.position, target.transform.position) > lostTargetDistance)
    {
        idleTimer = 0f;
        SwitchState(NPCState.Scan);
        return;
    }

    idleTimer += Time.deltaTime;
    if (idleTimer > 2f)
    {
        idleTimer = 0f;
        currentTrial++;

        Debug.Log($"Trial {currentTrial}/{totalTrials} complete");
        
        if (currentTrial >= totalTrials)
        {
            currentTrial = 0;
            agent.isStopped = true;

            Debug.Log("All epochs done — stopping");
        }
        else
        {
            SwitchState(NPCState.Return);
        }
    }
}

    void UpdateFaceReturn()
    {
        Vector3 dir = (patrolOrigin - transform.position).normalized;
        dir.y = 0;
        if (dir == Vector3.zero) { SwitchState(NPCState.Return); return; }

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, faceSpeed * Time.deltaTime);

        if (Quaternion.Angle(transform.rotation, targetRot) < faceAngleThreshold)
            SwitchState(NPCState.Return);
    }

    void UpdateReturn()
    {
        agent.isStopped = false;
        agent.SetDestination(patrolOrigin);

        if (!agent.pathPending && agent.remainingDistance < 0.5f)
        {
            Debug.Log("[FSM] Returned to origin");

            if (currentTrial >= totalTrials)
            {
                agent.isStopped = true;
                Debug.Log($"=== All done! Total finds: {currentTrial}/{totalTrials} ===");
                return;
            }

            SwitchState(NPCState.Scan);
        }
    }

    void UpdateFullScanAtSpot()
    {
        if (scanDone)
        {
            if (hasDestination)
                SwitchState(NPCState.FaceDirection);
            else
                SwitchState(NPCState.FaceReturn);
            return;
        }

        float step = rotateSpeed * Time.deltaTime;
        transform.Rotate(0, step, 0);
        scannedAngle += step;

        if (hasDestination)
        {
            scanDone = true;
            return;
        }

        if (scannedAngle >= 720f) // 2 full rotations
            scanDone = true;
    }

    public void SwitchState(NPCState newState)
    {
        Debug.Log($"[FSM] {currentState} → {newState}");
        currentState = newState;

        switch (newState)
        {
            case NPCState.Scan:
                EnterScan();
                break;
            case NPCState.FaceDirection:
                agent.isStopped = true;
                if (sensor != null) sensor.canMove = false;
                break;
            case NPCState.Move:
                moveCount++;
                if (target != null)
                {
                    distanceBeforeMove = Vector3.Distance(transform.position, target.transform.position);
                    Debug.Log($"[Move] distBeforeMove:{distanceBeforeMove:F2} target:{target.name}");
                }
                else
                {
                    distanceBeforeMove = -1f;
                }
                lastMoveWasCorrect = false;
                hasSearched = true;
                agent.updateRotation = true;
                agent.isStopped = false;
                agent.SetDestination(moveDestination);
                if (sensor != null) sensor.canMove = true;
                break;
            case NPCState.Chase:
                if (target != null && distanceBeforeMove > 0)
                {
                    float distAfter = Vector3.Distance(transform.position, target.transform.position);
                    bool closer = distAfter < distanceBeforeMove;
                    if (closer) correctMoveCount++;
                    Debug.Log($"[Move→Chase] distBefore:{distanceBeforeMove:F2} distAfter:{distAfter:F2} closer:{closer}");
                    distanceBeforeMove = -1f;
                }
                agent.isStopped = false;
                agent.updateRotation = true;
                if (sensor != null) sensor.canMove = false;
                break;
            case NPCState.FaceReturn:
                agent.isStopped = true;
                if (sensor != null) sensor.canMove = false;
                break;
            case NPCState.Return:
                agent.isStopped = false;
                agent.updateRotation = true;
                if (sensor != null) sensor.canMove = false;
                break;
            case NPCState.Idle:
                agent.isStopped = true;
                if (sensor != null) sensor.canMove = false;
                break;

            case NPCState.LocalScan:
                scanCount++;
                agent.isStopped = true;
                agent.updateRotation = false;
                scannedAngle = 0f;
                scanDone = false;
                hasDestination = false;
                bestScore = 0f;
                bestDestination = Vector3.zero;
                if (sensor != null) sensor.canMove = false;
                break;
            
            case NPCState.FullScanAtSpot:
            scanCount++;
            agent.isStopped = true;
            agent.updateRotation = false;
            scannedAngle = 0f;
            scanDone = false;
            hasDestination = false;
            bestScore = 0f;
            bestDestination = Vector3.zero;
            if (sensor != null) sensor.canMove = false;
            break;
            
        }
    }

    IEnumerator LogEpochHTTP(int epoch, bool found, float time, int scans, bool valid)
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        string json = $"{{\"scene\":\"{scene}\",\"epoch\":{epoch},\"found\":{found.ToString().ToLower()},\"time\":{time:F1},\"scans\":{scans},\"moves\":{moveCount},\"correct_moves\":{correctMoveCount},\"valid\":{valid.ToString().ToLower()}}}";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        using var req = UnityWebRequest.Put("http://127.0.0.1:8000/log_epoch", bodyRaw);
        req.method = "POST";
        req.SetRequestHeader("Content-Type", "application/json");
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("HTTP log failed, writing locally");
            LogEpochLocal(epoch, found, time, scans, valid);
        }
    }

    void LogEpochLocal(int epoch, bool found, float time, int scans, bool valid = true)
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        string line = $"{System.DateTime.Now:HH:mm:ss},{scene},{epoch},{found},{time:F1},{scans},{moveCount},{valid}\n";
        string path = Application.dataPath + "/results.csv";
        System.IO.File.AppendAllText(path, line);
        Debug.Log($"Logged epoch: {line.Trim()}");
    }
    
    // called by NPCSensorScript when new destination received from server
    public void SetMoveDestination(Vector3 dest, float score, Vector3 capturedPos, Vector3 capturedTargetPos, bool targetInView)
    {
        if (score > bestScore)
        {
            bestScore = score;
            bestDestination = dest;
            lastCapturedTargetPos = capturedTargetPos;
            lastMoveWasCorrect = targetInView;
        }
        hasDestination = bestDestination != Vector3.zero;
        moveDestination = bestDestination;
    }
}