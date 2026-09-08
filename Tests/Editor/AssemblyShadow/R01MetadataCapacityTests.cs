using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R01MetadataCapacityTests
    {
        private static MetadataEncodingProfile Profile()
        {
            return MetadataEncodingProfile.CreateV1(new string('a', 40), "helper-sha");
        }

        private static MetadataCapacityInput Input(string name, ulong bytes)
        {
            return new MetadataCapacityInput { name = name, bytes = bytes, dllSize = bytes, sha256 = name + "-sha" };
        }

        [Test]
        public void VersionOneProfileMatchesNativeEncoding()
        {
            var profile = Profile();
            CollectionAssert.AreEqual(new[] { 6, 4, 2, 0 }, profile.extraShiftBits);
            CollectionAssert.AreEqual(new[] { 64, 16, 4, 1 }, profile.kindStrides);
            CollectionAssert.AreEqual(new uint[] { 256, 256, 256, 255 }, profile.kindLimits);
            CollectionAssert.AreEqual(new uint[] { 64, 0, 0, 0 }, profile.initialCursors);
            Assert.AreEqual(4, profile.sizeMultiplier);
        }

        [Test]
        public void ThresholdsUseKindThreeTowardZeroFallbackOrder()
        {
            var report = MetadataCapacityPlanner.Plan(Profile(), new[]
            {
                Input("tiny", 1),
                Input("oneMiBMinusOne", 1UL * 1024UL * 1024UL - 1),
                Input("kind2", 1024UL * 1024UL),
                Input("fourMiBMinusOne", 4UL * 1024UL * 1024UL - 1),
                Input("kind1", 4UL * 1024UL * 1024UL),
                Input("sixteenMiBMinusOne", 16UL * 1024UL * 1024UL - 1),
                Input("kind0", 16UL * 1024UL * 1024UL),
            });
            Assert.IsTrue(report.fits);
            CollectionAssert.AreEqual(new[] { 3, 3, 2, 2, 1, 1, 0 }, report.allocations.Select(item => item.kind).ToArray());

            var oversize = MetadataCapacityPlanner.Plan(Profile(), new[] { Input("too-large", 64UL * 1024UL * 1024UL) });
            Assert.IsFalse(oversize.fits);
            Assert.AreEqual("Oversize", oversize.failureReason);
            Assert.AreEqual(0, oversize.firstFailingIndex);
        }

        [Test]
        public void FreshHomogeneousCapacityMatchesEachNativeBucketBoundary()
        {
            var cases = new[]
            {
                new { size = 1UL, count = 338 },
                new { size = 1UL * 1024UL * 1024UL, count = 83 },
                new { size = 4UL * 1024UL * 1024UL, count = 19 },
                new { size = 16UL * 1024UL * 1024UL, count = 3 },
            };
            foreach (var testCase in cases)
            {
                var report = MetadataCapacityPlanner.Plan(Profile(), Enumerable.Range(0, testCase.count).Select(i => Input("A" + i, testCase.size)).ToArray());
                Assert.IsTrue(report.fits, "size=" + testCase.size);
                var exhausted = MetadataCapacityPlanner.Plan(Profile(), new[] { Input("after", testCase.size) }, report.cursorsAfter);
                Assert.IsFalse(exhausted.fits, "size=" + testCase.size);
                Assert.AreEqual("Exhausted", exhausted.failureReason);
            }
        }

        [Test]
        public void FullKindThreeFallsBackToKindTwoWithoutReorderingInputs()
        {
            var cursors = new uint[] { 64, 0, 0, 254 };
            var report = MetadataCapacityPlanner.Plan(Profile(), new[] { Input("first", 1), Input("second", 1) }, cursors);
            Assert.IsTrue(report.fits);
            CollectionAssert.AreEqual(new[] { "first", "second" }, report.allocations.Select(item => item.name).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2 }, report.allocations.Select(item => item.kind).ToArray());
            CollectionAssert.AreEqual(new uint[] { 64, 0, 4, 255 }, report.cursorsAfter);
        }

        [Test]
        public void StressCountsExposeTheSharedBoundedCapacity()
        {
            foreach (int count in new[] { 100, 300 })
            {
                var report = MetadataCapacityPlanner.Plan(Profile(), Enumerable.Range(0, count).Select(i => Input("A" + i, 1)).ToArray());
                Assert.IsTrue(report.fits, "count=" + count);
            }
            var exhausted = MetadataCapacityPlanner.Plan(Profile(), Enumerable.Range(0, 1000).Select(i => Input("A" + i, 1)).ToArray());
            Assert.IsFalse(exhausted.fits);
            Assert.AreEqual(338, exhausted.firstFailingIndex);
            Assert.AreEqual("A338", exhausted.firstFailingAssembly);
            Assert.AreEqual("Exhausted", exhausted.failureReason);
        }

        [Test]
        public void OrdinaryInputsUseVerifiedFileLengthAndFreshHashInReceiptOrder()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssemblyShadowMetadataCapacity-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Assemblies"));
                File.WriteAllBytes(Path.Combine(root, "Assemblies", "Ordinary.dll"), new byte[] { 1, 2, 3, 4, 5 });
                string hash = ShadowHash.File(Path.Combine(root, "Assemblies", "Ordinary.dll"));
                var receipt = new AssemblySnapshotReceipt
                {
                    normalHotUpdateAssemblies = new[] { "Ordinary" },
                    assemblies = new[] { new SnapshotFile { name = "Ordinary", path = "Assemblies/Ordinary.dll", sha256 = hash } },
                    filteredAssemblies = new[] { new SnapshotFile { name = "nunit.framework", path = "Assemblies/Filtered/nunit.framework.dll", sha256 = "filtered" } },
                };
                var inputs = MetadataCapacityPlanner.OrdinaryInputs(root, receipt);
                Assert.AreEqual(1, inputs.Length);
                Assert.AreEqual((ulong)5, inputs[0].bytes);
                Assert.AreEqual(hash, inputs[0].sha256);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void InvalidSizesFailBeforeMultiplicationAndPublicationGuardNamesFirstMember()
        {
            foreach (var item in new[] { Input("zero", 0), Input("overflow", ulong.MaxValue), Input("oversize", 64UL * 1024UL * 1024UL) })
            {
                var report = MetadataCapacityPlanner.Plan(Profile(), new[] { item });
                Assert.IsFalse(report.fits, item.name);
                Assert.AreEqual(0, report.firstFailingIndex);
            }
            var failed = MetadataCapacityPlanner.Plan(Profile(), Enumerable.Range(0, 1000).Select(i => Input("A" + i, 1)).ToArray());
            var error = Assert.Throws<ShadowBuildException>(() => MetadataCapacityPlanner.RequireFits(failed));
            Assert.AreEqual("MetadataCapacityExceeded", error.Code);
            StringAssert.Contains("A338", error.Message);
        }
    }
}
