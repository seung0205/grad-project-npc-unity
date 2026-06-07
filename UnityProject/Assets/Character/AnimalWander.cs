using UnityEngine;
using UnityEngine.AI;

public class AnimalWander : MonoBehaviour
{
    public float wanderRadius = 5f;
    public float wanderInterval = 4f;
    public float idleTime = 2f;

    private NavMeshAgent agent;
    private Animator animator;
    private Vector3 origin;

    private enum WanderState { Idle, Walking }
    private WanderState state = WanderState.Idle;
    private float timer = 0f;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        origin = transform.position;
        timer = idleTime;
        agent.isStopped = true;
        agent.updateRotation = false;
        agent.angularSpeed = 120f;
        animator.applyRootMotion = false;
    }

    void Update()
    {
        if (agent.velocity.sqrMagnitude > 0.1f)
        {
            Quaternion targetRot = Quaternion.LookRotation(agent.velocity.normalized);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 5f);
        }
        timer -= Time.deltaTime;
        //Debug.Log($"WanderState: {state}, velocity: {agent.velocity.magnitude}");

        switch (state)
        {
            case WanderState.Idle:
                agent.isStopped = true;
                SetAnimation(0f);

                if (timer <= 0f)
                {
                    Vector3 dest = GetRandomPoint();
                    if (dest != Vector3.zero)
                    {
                        agent.isStopped = false;
                        agent.SetDestination(dest);
                        state = WanderState.Walking;
                        timer = wanderInterval;
                    }
                    else
                    {
                        timer = idleTime; // retry
                    }
                }
                break;

            case WanderState.Walking:
                SetAnimation(agent.velocity.magnitude);

                // arrived
                bool arrived = !agent.pathPending && agent.hasPath && agent.remainingDistance <= agent.stoppingDistance;
                if (arrived || timer <= 0f)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                    state = WanderState.Idle;
                    timer = idleTime;
                    SetAnimation(0f);
                }
                break;
        }
    }

    Vector3 GetRandomPoint()
    {
        for (int i = 0; i < 10; i++)
        {
            Vector2 rand2D = Random.insideUnitCircle * wanderRadius * 0.8f;
            Vector3 candidate = origin + new Vector3(rand2D.x, 0, rand2D.y);

            NavMeshHit hit;
            if (NavMesh.SamplePosition(candidate, out hit, 1f, NavMesh.AllAreas))
            {
                if (Vector3.Distance(hit.position, origin) < wanderRadius * 0.8f)
                    return hit.position;
            }
        }
        return Vector3.zero;
    }

    void SetAnimation(float speed)
    {
        if (animator == null) return;
        
        // 곰은 isWalking bool 사용, 다른 동물은 Vert/State float 사용
        if (HasParameter("isWalking"))
        {
            animator.SetBool("isWalking", speed > 0.1f);
        }
        else
        {
            float normalizedSpeed = speed / agent.speed;
            animator.SetFloat("Vert", normalizedSpeed);
            animator.SetFloat("State", 0f);
        }
    }

    bool HasParameter(string name)
    {
        foreach (var p in animator.parameters)
            if (p.name == name) return true;
        return false;
    }

    void OnDrawGizmos()
    {
        Vector3 center = Application.isPlaying ? origin : transform.position;
        Gizmos.color = new Color(0f, 0.5f, 1f, 0.3f);
        Gizmos.DrawSphere(center, wanderRadius);
        Gizmos.color = new Color(0f, 0.5f, 1f, 1f);
        Gizmos.DrawWireSphere(center, wanderRadius);
    }
}