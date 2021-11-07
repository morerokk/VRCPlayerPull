
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class LeashNeckAttach : UdonSharpBehaviour
{
    [UdonSynced]
    public bool Attached = false;

    /// <summary>
    /// World-space position offset from the attached neck. This is programmatically set when the collar is attached.
    /// </summary>
    [UdonSynced]
    public Vector3 PositionOffset;

    /// <summary>
    /// World-space rotation offset from the attached neck. This is programmatically set when the collar is attached.
    /// </summary>
    [UdonSynced]
    public Quaternion RotationOffset;

    // Needed to get a list of all players so we can get the closest player. Please tell me a better way to do this if you know one.
    private VRCPlayerApi[] playerList = new VRCPlayerApi[60];

    /// <summary>
    /// Max distance to neck bone when you want to attach the collar.
    /// </summary>
    public float MaxDistanceToNeck = 0.5f;

    /// <summary>
    /// How long the leash is before it starts pulling.
    /// </summary>
    [UdonSynced]
    public float LeashLength = 1.5f;

    /// <summary>
    /// The leash will always pull *at least* this hard on the attached player when he goes out of range.
    /// If VariablePullStrength is true, the actual pull strength *could* be higher.
    /// </summary>
    public float MinPullStrength = 4f;

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
    /// During FixedUpdate, the value of this field will be what the collar position was during the previous FixedUpdate.
    /// </summary>
    private Vector3 oldCollarPosition;

    private Vector3 oldPlayerPosition;

    /// <summary>
    /// The transform that defines the collar mesh. This object will be scaled based on the scale slider.
    /// </summary>
    public Transform CollarMeshTransform;

    private VRC_Pickup collarPickup;

    private VRCPlayerApi currentOwner;

    // Below: extra properties that can be adjusted by the owner to change the scale etc.
    #region CustomizableProperties
    [UdonSynced]
    public Vector3 Scale = new Vector3(1,1,1);

    [UdonSynced]
    public bool CanPickupWhenAttached = true;
    #endregion

    #region UISettingReferences
    public Slider ScaleSlider;

    public Toggle CanPickupWhenAttachedToggle;

    public Slider LeashLengthSlider;
    #endregion

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
        oldCollarPosition = this.transform.position;
        oldPlayerPosition = Networking.LocalPlayer.GetPosition();
        collarPickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
        currentOwner = Networking.GetOwner(this.gameObject);

        UpdateLeashLine();
    }

    public override void OnDeserialization()
    {
        UpdateCollarTransform();
        UpdateLeashSettings();
        // On deserialization, if the leash is not attached, update the line renderer so that the line matches.
        // If the leash is attached, this will already happen anyway.
        if (!Attached)
        {
            UpdateLeashLine();
        }
    }

    private void UpdateCollarTransform()
    {
        // Update the mesh based on the new transform offsets
        if (CollarMeshTransform == null)
        {
            return;
        }

        CollarMeshTransform.localScale = Scale;
    }

    private void UpdateLeashSettings()
    {
        if (!CanPickupWhenAttached && Attached)
        {
            this.collarPickup.pickupable = false;
        }
        else
        {
            this.collarPickup.pickupable = true;
        }
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
        var neckPositionWorldspace = closestFoundPlayer.GetBonePosition(HumanBodyBones.Neck);
        var neckRotationWorldspace = closestFoundPlayer.GetBoneRotation(HumanBodyBones.Neck);

        // Set position/rotation offsets for the collar
        PositionOffset = this.transform.position - neckPositionWorldspace;
        RotationOffset = Quaternion.Inverse(this.transform.rotation) * neckRotationWorldspace;

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

        // Skip updating the leash line if the collar isn't being worn/held and the leash handle isn't being held.
        // Avoids some expensive calculations.
        // NOTE: This is probably no longer necessary if the line were drawn via shader instead.
        if (this.collarPickup.IsHeld || this.LeashHandle.IsHeld || Attached)
        {
            UpdateLeashLine();
        }
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

        this.transform.position = neckPosition + PositionOffset;
        this.transform.rotation = neckRotation * RotationOffset;

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

        // Use the dedicated attachment points if available. Otherwise, use the collar transform and leash handle transform.
        Vector3 collarPoint;
        if (CollarLineAttachPoint != null)
        {
            collarPoint = CollarLineAttachPoint.position;
        }
        else
        {
            collarPoint = this.transform.position;
        }

        Vector3 handlePoint;
        if (LeashHandleLineAttachPoint != null)
        {
            handlePoint = LeashHandleLineAttachPoint.position;
        }
        else
        {
            handlePoint = LeashHandle.transform.position;
        }

        // Make a smooth curve between 3 points (collar, midway, handle).
        // The midway point rises up as the leash gets pulled further away, or down if it gets closer.
        // Note: perhaps a shader would have been a lot more performant for this...
        float dist = Vector3.Distance(collarPoint, handlePoint);

        // Calculate the needed curve and set each point
        float curveHeight = Mathf.Max(Mathf.Sqrt(LeashLength - dist), 0f);

        LeashLine.SetPosition(0, collarPoint);

        // An even amount of points is recommended.
        // TODO: Adjust this based on leash length? Or maybe this whole thing should just be a shader to begin with...
        int linePointAmount = LeashLine.positionCount;

        // Calculate where the line midpoints should be.
        for (int i = 1; i < linePointAmount - 1; i++)
        {
            float samplePointPerStep = (1f / (linePointAmount - 1));
            float samplePoint = samplePointPerStep * i;

            LeashLine.SetPosition(i, SampleParabola(collarPoint, handlePoint, curveHeight, samplePoint, new Vector3(0, -1, 0)));
        }
        LeashLine.SetPosition(linePointAmount - 1, handlePoint);
    }

    // Copied from https://forum.unity.com/threads/drawing-an-arc-from-point-to-point.1000336/#post-6503648
    /// <summary>
    /// Samples a parabolic curve.
    /// </summary>
    /// <param name="start">The start of the curve.</param>
    /// <param name="end">The end of the curve.</param>
    /// <param name="height">The curve's height.</param>
    /// <param name="t">Normalized length of where to sample the curve (0-1, with 0 being the start)</param>
    /// <param name="outDirection">The direction that the curve should go towards.</param>
    /// <returns>A point on the parabola.</returns>
    private Vector3 SampleParabola(Vector3 start, Vector3 end, float height, float t, Vector3 outDirection)
    {
        float parabolicT = t * 2 - 1;
        //start and end are not level, gets more complicated
        Vector3 travelDirection = end - start;
        Vector3 levelDirection = end - new Vector3(start.x, end.y, start.z);
        Vector3 right = Vector3.Cross(travelDirection, levelDirection);
        Vector3 up = outDirection;
        Vector3 result = start + t * travelDirection;
        result += ((-parabolicT * parabolicT + 1) * height) * up.normalized;
        return result;
    }

    void FixedUpdate()
    {
        if (Attached && Networking.IsOwner(Networking.LocalPlayer, this.gameObject) && LeashHandle != null)
        {
            CheckLeashMoveOutOfRange();
            CheckLeashMoveWhileOutOfRange();
            CheckLeashPull();
        }

        oldHandlePosition = LeashHandle.transform.position;
        oldCollarPosition = this.transform.position;
        oldPlayerPosition = Networking.LocalPlayer.GetPosition();
    }

    /// <summary>
    /// Check if the player *only just now* moved out of the leash range.
    /// If so, teleport them back.
    /// </summary>
    private void CheckLeashMoveOutOfRange()
    {
        // If the player was in range last update but out of range last update, teleport them back to the last "in range" position.
        var oldDist = Vector3.Distance(this.LeashHandle.transform.position, this.oldCollarPosition);
        var newDist = Vector3.Distance(this.LeashHandle.transform.position, this.transform.position);

        if(oldDist > LeashLength || newDist <= LeashLength)
        {
            // We were either out of range last update, or in range in the current update. Either way, no longer applies.
            return;
        }

        // We moved out of range, teleport back in.
        Networking.LocalPlayer.TeleportTo(oldPlayerPosition, Networking.LocalPlayer.GetRotation());
    }

    /// <summary>
    /// Block any movements made by the player while outside the leash range, unless it's towards the leash handle.
    /// </summary>
    private void CheckLeashMoveWhileOutOfRange()
    {
        var oldDist = Vector3.Distance(this.LeashHandle.transform.position, this.oldCollarPosition);
        var newDist = Vector3.Distance(this.LeashHandle.transform.position, this.transform.position);
        if (oldDist <= LeashLength || newDist <= LeashLength)
        {
            // We are either in range *now*, or we were in range last update. Either way, this function should not apply.
            return;
        }

        // We made a movement while being stuck outside the leash range.
        // Check if this movement gets the player closer to the leash handle. If so, allow it.
        if(newDist < oldDist)
        {
            return;
        }

        // Player tried to move further away from the leash handle, so pull them back.
        Networking.LocalPlayer.TeleportTo(oldPlayerPosition, Networking.LocalPlayer.GetRotation());
    }

    private void CheckLeashPull()
    {
        // Move the attached player if the leash handle is pulled too far.
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
        if (Utilities.IsValid(Networking.LocalPlayer))
        {
            PullPlayer(velocity.magnitude);
        }
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

    #region UIEvents
    public void OnUIOffsetsChanged()
    {
        // Update the scale based on the UI sliders.
        // NOTE: Will only work for the person holding or wearing the collar!
        if (ScaleSlider != null)
        {
            Scale = new Vector3(ScaleSlider.value, ScaleSlider.value, ScaleSlider.value);
        }

        UpdateCollarTransform();

        if (!Attached)
        {
            UpdateLeashLine();
        }
    }

    public void OnLeashSettingsChanged()
    {
        if (CanPickupWhenAttachedToggle != null)
        {
            CanPickupWhenAttached = CanPickupWhenAttachedToggle.isOn;
        }

        if (LeashLengthSlider != null)
        {
            LeashLength = LeashLengthSlider.value;
        }

        UpdateLeashSettings();

        if (!Attached)
        {
            UpdateLeashLine();
        }
    }
    #endregion
}
