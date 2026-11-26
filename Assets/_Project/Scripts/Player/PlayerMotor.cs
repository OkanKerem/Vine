using UnityEngine;

namespace Vineyard.Player
{
    /// <summary>
    /// Deterministic-ish movement step shared by the host (authoritative) and the
    /// owning client (prediction / replay). No networking in here.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        [Header("Movement")]
        public float walkSpeed = 5f;
        public float sprintSpeed = 8.5f;
        public float jumpHeight = 1.2f;
        public float gravity = 20f;
        [Tooltip("Small downward velocity while grounded so isGrounded stays stable.")]
        public float groundedStickVelocity = -2f;

        public CharacterController Controller { get; private set; }
        public float VerticalVelocity { get; set; }

        void Awake()
        {
            Controller = GetComponent<CharacterController>();
        }

        /// <summary>Advance the character by one tick using the given input.</summary>
        public void Step(in PlayerInputCmd cmd, float dt)
        {
            Quaternion yawRot = Quaternion.Euler(0f, cmd.yaw, 0f);
            Vector3 wish = yawRot * new Vector3(cmd.moveX, 0f, cmd.moveY);
            wish = Vector3.ClampMagnitude(wish, 1f);
            // Team upgrades (replicated levels): the host and the predicting client use the same values.
            float speed = (cmd.sprint ? sprintSpeed : walkSpeed) * Vineyard.Upgrades.TeamUpgrades.SpeedMultiplier;

            if (Controller.isGrounded)
            {
                if (VerticalVelocity < 0f) VerticalVelocity = groundedStickVelocity;
                if (cmd.jump) VerticalVelocity = Mathf.Sqrt(2f * jumpHeight * Vineyard.Upgrades.TeamUpgrades.JumpMultiplier * gravity);
            }

            VerticalVelocity -= gravity * dt;

            Vector3 velocity = wish * speed + Vector3.up * VerticalVelocity;
            CollisionFlags flags = Controller.Move(velocity * dt);

            // Hitting a ceiling kills upward velocity.
            if ((flags & CollisionFlags.Above) != 0 && VerticalVelocity > 0f)
                VerticalVelocity = 0f;
        }

        /// <summary>Hard-set position (CharacterController ignores transform writes while enabled).</summary>
        public void Teleport(Vector3 position)
        {
            Controller.enabled = false;
            transform.position = position;
            Controller.enabled = true;
        }
    }
}
