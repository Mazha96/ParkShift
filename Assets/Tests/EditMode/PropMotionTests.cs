using NUnit.Framework;
using UnityEngine;
using ParkShift.Attraction;
using System.Collections;
using UnityEngine.TestTools;

namespace ParkShift.Tests
{
    public class PropMotionTests
    {
        [UnityTearDown] public IEnumerator LeavePlayMode()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator OpenDoorChecksTheWholeClosingSweepForCrew()
        {
            yield return new EnterPlayMode();
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var crew = new GameObject("Crew in doorway");
            var root = new GameObject("Door driver");
            try
            {
                panel.transform.position = new Vector3(1000, 1.525f, 1000);
                panel.transform.localScale = new Vector3(1.7f, 2.1f, 0.12f);
                var driver = root.AddComponent<UnloadDoorDriver>();
                driver.DoorPanel = panel.transform;
                driver.OpenOffset = Vector3.left * 2f;
                yield return null; // real Start lifecycle initializes the kinematic panel
                driver.enabled = false; // isolate clearance geometry from the active scene's sim
                panel.GetComponent<Rigidbody>().position = new Vector3(998, 1.525f, 1000);
                crew.transform.position = new Vector3(1000, 1, 1000);
                crew.AddComponent<CharacterController>();
                Physics.SyncTransforms();
                Assert.That(Time.timeScale, Is.EqualTo(1f), "The clearance test requires normal simulation time.");
                float fixedTime = Time.fixedTime;
                double deadline = Time.realtimeSinceStartupAsDouble + 2d;
                while (Time.fixedTime <= fixedTime && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.Greater(Time.fixedTime, fixedTime, "Play Mode must advance physics before the query.");
                Assert.That(panel.GetComponent<Collider>().bounds.center.x, Is.EqualTo(998f).Within(0.01f));
                Assert.IsFalse(driver.CanClose);
                crew.transform.position += Vector3.right * 5f;
                Physics.SyncTransforms();
                Assert.IsTrue(driver.CanClose);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(crew);
                UnityEngine.Object.DestroyImmediate(panel);
            }
        }

        static Hinge CreateHinge(Transform t) => new Hinge(t, new Vector3(95, 0, 0)) { Overshoot = 0.7f };
        static void Tick(Hinge hinge, bool target, float dt) => hinge.Tick(target, dt, 2.5f);

        [Test] public void HingeMatchesAcrossFrameSizesAndReturnsToImportedRest()
        {
            var a = new GameObject("Small steps");
            var b = new GameObject("Large steps");
            try
            {
                Quaternion rest = Quaternion.Euler(-90, 21, 8);
                a.transform.localRotation = b.transform.localRotation = rest;
                Hinge first = CreateHinge(a.transform), second = CreateHinge(b.transform);
                for (int i = 0; i < 20; i++) Tick(first, true, 0.01f);
                Tick(second, true, 0.2f);
                Assert.Less(Quaternion.Angle(a.transform.localRotation, b.transform.localRotation), 0.05f);
                Quaternion before = a.transform.localRotation;
                Tick(first, false, 0f);
                Assert.Less(Quaternion.Angle(before, a.transform.localRotation), 0.05f);
                for (int cycle = 0; cycle < 25; cycle++)
                {
                    Tick(first, true, 2f);
                    Tick(first, false, 2f);
                }
                Assert.Less(Quaternion.Angle(rest, a.transform.localRotation), 0.05f);
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }

        [Test] public void RepeatedPressNeverReleasesTheButtonOnTheTriggerFrame()
        {
            var button = new GameObject("Button");
            try
            {
                button.transform.localScale = new Vector3(2, 3, 4);
                var pulse = new PressPulse { T = button.transform };
                pulse.Trigger();
                pulse.Tick(0.016f);
                float pressed = button.transform.localScale.z;
                Assert.Less(pressed, 4f);
                pulse.Trigger();
                pulse.Tick(0f);
                Assert.LessOrEqual(button.transform.localScale.z, pressed + 0.001f);
                for (int i = 0; i < 100; i++) pulse.Tick(0.02f);
                Assert.AreEqual(new Vector3(2, 3, 4), button.transform.localScale);
            }
            finally { UnityEngine.Object.DestroyImmediate(button); }
        }
    }
}
