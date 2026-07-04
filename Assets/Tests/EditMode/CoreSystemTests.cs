using NUnit.Framework;
using ParkShift.Core.Testing;

namespace ParkShift.Tests
{
    /// <summary>NUnit wrappers over the engine-agnostic CoreTestSuite (single source of truth,
    /// also runnable headless under mono/dotnet without Unity).</summary>
    public class CoreSystemTests
    {
        void Run(System.Action test)
        {
            CoreTestSuite.Failures.Clear();
            test();
            Assert.IsEmpty(CoreTestSuite.Failures, string.Join("\n", CoreTestSuite.Failures));
        }

        [Test] public void StateTransitions() => Run(CoreTestSuite.Test_StateTransitions);
        [Test] public void BlockOccupancy() => Run(CoreTestSuite.Test_BlockOccupancy);
        [Test] public void DispatchRules() => Run(CoreTestSuite.Test_DispatchRules);
        [Test] public void BlockHoldAndStacking() => Run(CoreTestSuite.Test_BlockHoldAndStacking);
        [Test] public void SeparateLoadUnload() => Run(CoreTestSuite.Test_SeparateLoadUnload);
        [Test] public void UnloadWorkflow() => Run(CoreTestSuite.Test_UnloadWorkflow);
        [Test] public void AddTrain() => Run(CoreTestSuite.Test_AddTrain);
        [Test] public void FaultTriggerAndResolution() => Run(CoreTestSuite.Test_FaultTriggerAndResolution);
        [Test] public void DispatchTimeoutFault() => Run(CoreTestSuite.Test_DispatchTimeoutFault);
        [Test] public void ScoreCalculation() => Run(CoreTestSuite.Test_ScoreCalculation);
        [Test] public void SoloTuning() => Run(CoreTestSuite.Test_SoloTuning);
        [Test] public void FullShiftEndsAndTelemetry() => Run(CoreTestSuite.Test_FullShiftEndsAndTelemetry);
    }
}
