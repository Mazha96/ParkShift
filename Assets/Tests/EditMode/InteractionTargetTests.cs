using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using ParkShift.Interaction;
using ParkShift.Player;

namespace ParkShift.Tests
{
    public class InteractionTargetStub : MonoBehaviour, IInteractable
    {
        public bool Available = true;
        public string GetPrompt(RoleController role) => Available ? "Use target" : null;
        public void Interact(RoleController role) { }
    }

    public class InteractionTargetTests
    {
        [Test] public void AimAssistFindsSmallTargetButCannotReachThroughSolidWalls()
        {
            var rig = new GameObject("Interaction test rig");
            var target = new GameObject("Small handle");
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                rig.transform.position = new Vector3(2000, 10, 2000);
                var camera = rig.AddComponent<Camera>();
                var interactor = rig.AddComponent<PlayerInteractor>();
                interactor.Cam = camera;
                target.transform.position = rig.transform.position + new Vector3(0.09f, 0f, 2f);
                var collider = target.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 0.08f;
                collider.isTrigger = true;
                var action = target.AddComponent<InteractionTargetStub>();
                wall.transform.position = rig.transform.position + Vector3.right * 5f;
                Physics.SyncTransforms();
                var method = typeof(PlayerInteractor).GetMethod("FindTarget", BindingFlags.Instance | BindingFlags.NonPublic);
                var ray = new Ray(camera.transform.position, Vector3.forward);
                Assert.IsNull(method.Invoke(interactor, new object[] { ray, false }));
                Assert.AreSame(action, method.Invoke(interactor, new object[] { ray, true }));
                wall.transform.position = rig.transform.position + Vector3.forward;
                Physics.SyncTransforms();
                Assert.IsNull(method.Invoke(interactor, new object[] { ray, true }));
                wall.transform.position += Vector3.right * 5f;
                action.Available = false;
                Physics.SyncTransforms();
                Assert.IsNull(method.Invoke(interactor, new object[] { ray, true }));
            }
            finally
            {
                Object.DestroyImmediate(rig); Object.DestroyImmediate(target); Object.DestroyImmediate(wall);
            }
        }
    }
}
