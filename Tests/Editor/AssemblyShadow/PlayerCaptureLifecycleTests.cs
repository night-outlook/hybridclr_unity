using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class PlayerCaptureLifecycleTests
    {
        [Test] public void MatchingPostprocessAndFinalPhasesStillPass()
        {
            Assert.DoesNotThrow(() => Invoke(State(false)));
            Assert.DoesNotThrow(() => Invoke(State(true)));
        }

        [Test] public void EveryOriginalPostprocessPredicateRemainsFailClosedAndNamed()
        {
            int[] indices = { 1, 2, 3, 5, 8, 10 };
            object[] changes = { false, false, true, "StandaloneWindows64", "different-guid", 3 };
            string[] names = { "linkedDirectoryPrepared", "afterFiltersCaptured", "postprocessObserved", "platform", "preprocessBuildGuid", "buildOptions" };
            for (int index = 0; index < indices.Length; index++)
            {
                var state = State(false); state[indices[index]] = changes[index];
                var exception = Assert.Throws<ShadowBuildException>(() => Invoke(state));
                Assert.That(exception.Message, Does.Contain("failed=[" + names[index] + "]"));
                Assert.That(exception.Message, Does.Contain("phase=OnPostprocessBuild"));
            }
        }

        [Test] public void FinalPhaseStillRequiresObservedPostprocessAndBothGuids()
        {
            foreach (int index in new[] { 3, 6, 7 })
            {
                var state = State(true); state[index] = index == 3 ? (object)false : "different-guid";
                var exception = Assert.Throws<ShadowBuildException>(() => Invoke(state));
                Assert.That(exception.Message, Does.Contain("phase=CompleteSuccessfulBuild"));
                Assert.That(exception.Message, Does.Contain(index == 3 ? "failed=[postprocessObserved]" : index == 6 ? "failed=[preprocessBuildGuid]" : "failed=[postprocessBuildGuid]"));
            }
        }

        [Test] public void DiagnosticIncludesAllFailuresAndExactExpectedObservedValues()
        {
            var state = State(false); state[1] = false; state[6] = "pre-guid"; state[8] = "post-guid"; state[10] = 3;
            var exception = Assert.Throws<ShadowBuildException>(() => Invoke(state));
            Assert.That(exception.Message, Does.Contain("failed=[linkedDirectoryPrepared,preprocessBuildGuid,buildOptions]"));
            Assert.That(exception.Message, Does.Contain("preprocessGuid=pre-guid"));
            Assert.That(exception.Message, Does.Contain("observedGuid=post-guid"));
            Assert.That(exception.Message, Does.Contain("expectedFilterOptions=1 ("));
            Assert.That(exception.Message, Does.Contain("Development"));
            Assert.That(exception.Message, Does.Contain("observedReportOptions=3 ("));
        }

        private static object[] State(bool completing)
        { return new object[] { completing, true, true, completing, "StandaloneOSX", "StandaloneOSX", "same-guid", "same-guid", "same-guid", (int)BuildOptions.Development, (int)BuildOptions.Development }; }

        private static void Invoke(object[] state)
        {
            var method = typeof(ShadowPlayerInputCapture).GetMethod("RequireBuildLifecycle", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            try { method.Invoke(null, state); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }
    }
}
