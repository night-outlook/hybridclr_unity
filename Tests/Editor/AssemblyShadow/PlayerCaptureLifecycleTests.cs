using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class PlayerCaptureLifecycleTests
    {
        private const string AssignedGuid = "256974a70b30447eba8a715227f8a6b3";
        private const string DifferentGuid = "356974a70b30447eba8a715227f8a6b3";
        [Test] public void MatchingPostprocessAndFinalPhasesStillPass()
        {
            Assert.DoesNotThrow(() => Invoke(State(false)));
            Assert.DoesNotThrow(() => Invoke(State(true)));
        }

        [Test] public void EveryOriginalPostprocessPredicateRemainsFailClosedAndNamed()
        {
            int[] indices = { 1, 2, 3, 5, 8, 10 };
            object[] changes = { false, false, true, "StandaloneWindows64", DifferentGuid, 3 };
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
                var state = State(true); state[index] = index == 3 ? (object)false : DifferentGuid;
                var exception = Assert.Throws<ShadowBuildException>(() => Invoke(state));
                Assert.That(exception.Message, Does.Contain("phase=CompleteSuccessfulBuild"));
                Assert.That(exception.Message, Does.Contain(index == 3 ? "failed=[postprocessObserved]" : index == 6 ? "failed=[preprocessBuildGuid]" : "failed=[postprocessBuildGuid]"));
            }
        }

        [Test] public void DiagnosticIncludesAllFailuresAndExactExpectedObservedValues()
        {
            var state = State(false); state[1] = false; state[6] = AssignedGuid; state[8] = DifferentGuid; state[10] = 3;
            var exception = Assert.Throws<ShadowBuildException>(() => Invoke(state));
            Assert.That(exception.Message, Does.Contain("failed=[linkedDirectoryPrepared,preprocessBuildGuid,buildOptions]"));
            Assert.That(exception.Message, Does.Contain("preprocessGuid=" + AssignedGuid));
            Assert.That(exception.Message, Does.Contain("observedGuid=" + DifferentGuid));
            Assert.That(exception.Message, Does.Contain("expectedFilterOptions=1 ("));
            Assert.That(exception.Message, Does.Contain("Development"));
            Assert.That(exception.Message, Does.Contain("observedReportOptions=3 ("));
        }

        [Test] public void UnassignedPreprocessGuidMayBecomeAssignedOnTheSameInvocation()
        {
            foreach (bool final in new[] { false, true })
            {
                var state = State(final); state[6] = new string('0', 32);
                Assert.DoesNotThrow(() => Invoke(state));
            }
        }

        [Test] public void PostprocessAndFinalGuidsMustBeAssignedAndWellFormed()
        {
            foreach (bool final in new[] { false, true })
                foreach (string invalid in new[] { null, "", new string('0', 32), "malformed", AssignedGuid.Substring(1), "256974a7-0b30-447e-ba8a-715227f8a6b3" })
                {
                    var state = State(final); state[6] = new string('0', 32); state[7] = invalid; state[8] = invalid;
                    Assert.That(Assert.Throws<ShadowBuildException>(() => Invoke(state)).Message, Does.Contain("assignedBuildGuid"));
                }
        }

        [Test] public void MalformedPreprocessOrChangedAssignedGuidCannotUseTheUnassignedTransition()
        {
            foreach (string invalid in new[] { null, "", "0", "malformed", DifferentGuid })
            {
                var state = State(false); state[6] = invalid;
                Assert.That(Assert.Throws<ShadowBuildException>(() => Invoke(state)).Message, Does.Contain("preprocessBuildGuid"));
            }
            var final = State(true); final[6] = new string('0', 32); final[7] = DifferentGuid;
            Assert.That(Assert.Throws<ShadowBuildException>(() => Invoke(final)).Message, Does.Contain("postprocessBuildGuid"));
        }

        [Test] public void UnassignedGuidNeverPermitsSwappedReportOrOutput()
        {
            foreach (bool final in new[] { false, true })
                foreach (int index in new[] { 11, 12, 13, 14 })
                {
                    var state = State(final); state[6] = new string('0', 32);
                    state[index] = index == 11 ? (object)0 : index == 12 ? (object)2469 : index == 13 ? null : "/tmp/OtherPlayer.app";
                    Assert.That(Assert.Throws<ShadowBuildException>(() => Invoke(state)).Message, Does.Contain(index < 13 ? "reportInstanceId" : "outputPath"));
                }
        }

        [Test] public void CanonicalOutputPathNormalizesLexicalAliasesButRejectsMissingPaths()
        {
            var method = typeof(ShadowPlayerInputCapture).GetMethod("CanonicalOutputPath", BindingFlags.Static | BindingFlags.NonPublic);
            string actual = (string)method.Invoke(null, new object[] { "/tmp/Build/../Player.app/" });
            Assert.AreEqual(System.IO.Path.GetFullPath("/tmp/Player.app"), actual);
            Assert.AreEqual(System.IO.Path.GetPathRoot(actual), method.Invoke(null, new object[] { System.IO.Path.GetPathRoot(actual) }));
            foreach (string invalid in new[] { null, "", " " })
                Assert.IsInstanceOf<ShadowBuildException>(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { invalid })).InnerException);
        }

        private static object[] State(bool completing)
        { return new object[] { completing, true, true, completing, "StandaloneOSX", "StandaloneOSX", AssignedGuid, AssignedGuid, AssignedGuid,
            (int)BuildOptions.Development, (int)BuildOptions.Development, 2468, 2468, "/tmp/Player.app", "/tmp/Player.app" }; }

        private static void Invoke(object[] state)
        {
            var method = typeof(ShadowPlayerInputCapture).GetMethod("RequireBuildLifecycle", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            try { method.Invoke(null, state); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }
    }
}
