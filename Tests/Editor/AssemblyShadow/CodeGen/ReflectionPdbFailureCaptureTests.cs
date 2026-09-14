using System;
using System.IO;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ReflectionPdbFailureCaptureTests
    {
        private string root;
        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "H1PdbCaptureTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private string Save(string[] references = null)
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            File.WriteAllText(Path.Combine(root, "ProjectSettings/AssemblyShadowReflectionBindings.json"), "{}");
            return ReflectionBindingFailureCapture.Save(root, "SyntheticCapture", new string[0], new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 },
                references ?? new string[0], root, new InvalidOperationException("outer", new ArgumentException("inner")));
        }
        [Test] public void CapturesAreUniqueAndDoNotGrantAcceptance()
        {
            string a = Save(), b = Save(); Assert.AreNotEqual(a, b);
            var value = ReflectionBindingFailureCapture.ReadVerified(a);
            Assert.IsFalse(value.buildProvenance || value.humanGatePassed || value.mayEnterR02);
        }
        [Test] public void FullInnerExceptionIsRetained()
        {
            string path = Save(); var value = ReflectionBindingFailureCapture.ReadVerified(path);
            StringAssert.Contains("outer", value.exception); StringAssert.Contains("inner", value.exception);
        }
        [Test] public void ChangedInputBytesFailHashVerification()
        {
            string path = Save(); File.WriteAllBytes(Path.Combine(path, "input.dll"), new byte[] { 9 });
            Assert.AreEqual("FailureCaptureHashMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingFailureCapture.ReadVerified(path)).Code);
        }
        [Test] public void ExtraMembersFailMembershipVerification()
        {
            string path = Save(); File.WriteAllText(Path.Combine(path, "extra.txt"), "extra");
            Assert.AreEqual("FailureCaptureMembershipMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingFailureCapture.ReadVerified(path)).Code);
        }
        [Test] public void MissingReferencesRemainUnavailable()
        {
            string path = Save(new[] { Path.Combine(root, "missing.dll") });
            Assert.AreEqual("IncompleteFailureCapture", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingFailureCapture.ReadVerified(path)).Code);
        }
        [Test] public void ProjectRelativeCompilerReferenceIsCapturedFromExactRoot()
        {
            string reference = Path.Combine(root, "reference.dll"); File.WriteAllBytes(reference, new byte[] { 7, 8, 9 });
            string path = Save(new[] { "reference.dll" });
            var capture = ReflectionBindingFailureCapture.ReadVerified(path);
            var retained = Array.Find(capture.files, value => value.role == "reference");
            Assert.IsNotNull(retained); Assert.AreEqual(Path.GetFullPath(reference), retained.sourcePath);
        }
    }
}
