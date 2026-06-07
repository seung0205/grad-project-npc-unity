using UnityEngine;
using UnityEngine.AI;

public class CharacterWander : MonoBehaviour
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
        Random.InitState(42);
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        origin = transform.position;
        timer = idleTime;
        agent.isStopped = true;
    }

    void Update()
    {
        timer -= Time.deltaTime;

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
        if (animator != null)
            animator.SetFloat("Speed", speed);
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