
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class LeashNeckAttach : UdonSharpBehaviour
{
    [UdonSynced]
    public bool Attached = false;

    // Needed to get a list of all players so we can get the closest player. Please tell me a better way to do this if you know one.
    private VRCPlayerApi[] playerList = new VRCPlayerApi[60];

    /// <summary>
    /// Max distance to neck bone when you want to attach the collar.
    /// </summary>
    public float MaxDistanceToNeck = 1f;

    /// <summary>
    /// How long the leash is before it starts pulling.
    /// </summary>
    public float LeashLength = 2f;

    /// <summary>
    /// The leash will always pull *at least* this hard on the attached player when he goes out of range.
    /// If VariablePullStrength is true, the actual pull strength *could* be higher.
    /// </summary>
    public float MinPullStrength = 2f;

    /// <summary>
    /// A multiplier for the leash pull velocity after all other calculations are done.
    /// </summary>
    public float VelocityMultiplier = 1f;

    /// <summary>
    /// If true, pulling the leash handle harder will pull the attached player harder (with a minimum velocity).
    /// If false, the pull strength is always the same as soon as the attached player goes out of range.
    /// </summary>
    public bool VariablePullStrength = true;

    /// <summary>
    /// A reference to the pickup that serves as the leash handle.
    /// </summary>
    public VRC_Pickup LeashHandle;

    /// <summary>
    /// A reference to the line renderer that will render the leash line.
    /// </summary>
    public LineRenderer LeashLine;

    /// <summary>
    /// If not null, will set this transform as "Point A" for the leash line.
    /// If null, the handle transform is used.
    /// </summary>
    public Transform LeashHandleLineAttachPoint;

    /// <summary>
    /// If not null, will set this transform as "Point B" for the leash line.
    /// If null, the handle transform is used.
    /// </summary>
    public Transform CollarLineAttachPoint;

    /// <summary>
    /// During FixedUpdate, the value of this field will be what the handle position was during the previous FixedUpdate.
    /// </summary>
    private Vector3 oldHandlePosition;

    /// <summary>
    /// If set, the debug rigidbody will be used for the leash mechanics if no player is attached.
    /// </summary>
    public Rigidbody DebugRigidBody;

    private VRC_Pickup collarPickup;

    private VRCPlayerApi currentOwner;

    void Start()
    {
        if(LeashHandle == null)
        {
            Debug.LogWarning("This collar has no leash handle!", this);
        }

        if (LeashLine == null)
        {
            Debug.LogWarning("This collar has no leash line!", this);
        }

        oldHandlePosition = LeashHandle.transform.position;
        collarPickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
        currentOwner = Networking.GetOwner(this.gameObject);
    }

    public override void OnPickupUseDown()
    {
        // On pickup use down, find the closest neck to attach to.
        // TODO: Is there a faster way to do this? Please tell me if you find one.
        // Currently, we iterate over *ALL* players and find the closest one. This probably isn't too bad because it only happens on use down, but still.
        playerList = VRCPlayerApi.GetPlayers(playerList);

        VRCPlayerApi closestFoundPlayer = null;
        float closestNeckDistance = 99999f;
        foreach(var player in playerList)
        {
            if(!Utilities.IsValid(player))
            {
                continue;
            }

            float distanceToNeck = Vector3.Distance(this.transform.position, player.GetBonePosition(HumanBodyBones.Neck));
            if(distanceToNeck < closestNeckDistance && distanceToNeck < MaxDistanceToNeck)
            {
                closestFoundPlayer = player;
                closestNeckDistance = distanceToNeck;
            }
        }

        if(!Utilities.IsValid(closestFoundPlayer))
        {
            return;
        }

        // If we reached this point, we found the closest neck to attach to. Make the collar stick to the new player.
        collarPickup.Drop();
        Attached = true;
        Networking.SetOwner(closestFoundPlayer, this.gameObject);
    }

    public override void OnPickup()
    {
        // Release collar when picked up
        Attached = false;
    }

    public override void PostLateUpdate()
    {
        // PostLateUpdate is called post-IK, meaning we can more accurately reposition the collar and leash line.
        UpdateCollarPosition();
        UpdateLeashLine();
    }

    private void UpdateCollarPosition()
    {
        // If attached, move the collar to the neck bone of the owner. Otherwise, don't do anything.
        if (!Attached)
        {
            return;
        }

        var owner = Networking.GetOwner(this.gameObject);
        if (!Utilities.IsValid(owner))
        {
            return;
        }

        var neckPosition = owner.GetBonePosition(HumanBodyBones.Neck);
        var neckRotation = owner.GetBoneRotation(HumanBodyBones.Neck);

        this.transform.position = neckPosition;
        this.transform.rotation = neckRotation;

        // Allow the owner to detach the collar on desktop by pressing X, since they probably cannot grab it at all if not in VR
        if (owner == Networking.LocalPlayer && Input.GetKey(KeyCode.X))
        {
            Attached = false;
        }
    }

    private void UpdateLeashLine()
    {
        // Update the line renderer A and B positions between the collar and leash handle
        if (LeashLine == null || LeashHandle == null)
        {
            return;
        }

        // Use the dedicated attachment points if available. Otherwise, use the collar transform and leash hande transform.
        if (CollarLineAttachPoint != null)
        {
            LeashLine.SetPosition(0, CollarLineAttachPoint.position);
        }
        else
        {
            LeashLine.SetPosition(0, this.transform.position);
        }

        if (LeashHandleLineAttachPoint != null)
        {
            LeashLine.SetPosition(1, LeashHandleLineAttachPoint.position);
        }
        else
        {
            LeashLine.SetPosition(1, LeashHandle.transform.position);
        }
    }

    void FixedUpdate()
    {
        CheckLeashPull();
        oldHandlePosition = LeashHandle.transform.position;
    }

    private void CheckLeashPull()
    {
        // Move the attached player if the leash handle is pulled too far.

        // Never do this if the collar isn't attached.
        if (!Attached)
        {
            return;
        }

        // Only do this if we are the owner.
        if (Networking.GetOwner(this.gameObject) != Networking.LocalPlayer)
        {
            return;
        }

        if (LeashHandle == null)
        {
            return;
        }

        var distance = Vector3.Distance(this.transform.position, this.LeashHandle.transform.position);
        if (distance <= LeashLength)
        {
            return;
        }

        // Distance limit exceeded, pull player back
        // Get the current velocity of the leash handle, and apply it to the player.
        // NOTE: It currently seems impossible to get the velocity of a held pickup via its RigidBody.
        // So instead, we get the velocity by comparing the old transform position and the new one.
        var media = (LeashHandle.transform.position - oldHandlePosition);
        var velocity = media / Time.fixedDeltaTime;

        // Pull back the attached player if possible.
        // In the editor, the player may not exist, but a debug rigidbody might be attached instead, so use that.
        if (Utilities.IsValid(Networking.LocalPlayer))
        {
            PullPlayer(velocity.magnitude);
        }
        else if (DebugRigidBody != null)
        {
            DebugRigidBody.velocity = velocity;
        }

        Debug.Log(string.Format("Doing leash pull, X: {0} Y: {1} Z: {2}", velocity.x, velocity.y, velocity.z), this);
    }

    private void PullPlayer(float leashHandleVelocity)
    {
        // If the player is out of the leash range, they should always be pulled back towards the leash handle.
        // However, the player holding the leash handle should also be able to "pull harder" (optionally).
        // So therefore:
        // Pull strength = the velocity of the leash handle or the minimum pull strength, whichever one is higher.
        // Directionality = dot(direction towards leash handle, direction of player velocity)
        // If the directionality is close enough, that means the player is already traveling towards the leash handle.
        // Additionally, if the player is then already traveling faster than Pull strength *and* the directionality is close enough, we don't pull the player.
        // The player must have already gotten pulled towards the leash.
        // This allows the leash to always apply a minimum pull strength, but *also* allows players to pull on the leash harder.
        // NOTE: This is all assuming that VariablePullStrength is true. If it's false, we just always pull the player closer with the same strength.

        // TODO: Should we take the direction from the player position rather than the leash position?
        // Pulling is a bit tricky because Udon ignores player horizontal velocity when grounded.
        Vector3 pullDirection = (LeashHandle.transform.position - this.transform.position).normalized;
        float pullStrength = 0f;
        if (VariablePullStrength)
        {
            pullStrength = Mathf.Max(leashHandleVelocity, MinPullStrength) * VelocityMultiplier;
        }
        else
        {
            pullStrength = MinPullStrength * VelocityMultiplier;
        }

        // Check if the player is already being pulled towards the leash at the appropriate direction *and* strength.
        var playerVelocity = Networking.LocalPlayer.GetVelocity();
        if(playerVelocity != Vector3.zero)
        {
            var playerDirection = playerVelocity.normalized;
            var playerSpeed = playerVelocity.magnitude;
            var dot = Vector3.Dot(playerDirection, pullDirection);
            if(dot < 0.95 && playerSpeed >= pullStrength)
            {
                // Player is already being pulled towards the leash at the right speed, therefore we shouldn't set the player's velocity.
                return;
            }
        }

        // Apply pull velocity to player
        var newVelocity = pullDirection * pullStrength;
        Networking.LocalPlayer.SetVelocity(newVelocity);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
    {
        // On ownership transfer, track the new owner so we can detach the leash if the owner leaves
        this.currentOwner = newOwner;
    }

    public override void OnPlayerLeft(VRCPlayerApi leavingPlayer)
    {
        // When the current owner leaves, detach the leash
        if(leavingPlayer == currentOwner)
        {
            Attached = false;
            currentOwner = Networking.GetOwner(this.gameObject);
        }
    }
}
