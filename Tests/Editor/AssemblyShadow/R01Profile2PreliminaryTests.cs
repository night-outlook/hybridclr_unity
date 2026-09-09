using System;
using System.Linq;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R01Profile2PreliminaryTests
    {
        private static MetadataCapacityProfile2Input Input(string name, ulong size)
        {
            return new MetadataCapacityProfile2Input
            {
                name = name,
                dllSize = size,
                sha256 = new string('a', 64),
            };
        }

        [Test]
        public void Profile2ConstantsIdentifySparse32AndSeparateAotDomain()
        {
            Assert.AreEqual(2, MetadataCapacityProfile2.ProfileVersion);
            Assert.AreEqual("SparseSignedInt32", MetadataCapacityProfile2.CodecId);
            Assert.AreEqual(-1, MetadataCapacityProfile2.InvalidIndexSentinel);
            Assert.AreEqual(int.MaxValue, MetadataCapacityProfile2.AotMaxIndex);
            Assert.AreEqual(1, MetadataCapacityProfile2.MinImageId);
            Assert.AreEqual(8192, MetadataCapacityProfile2.MaxImages);
            Assert.AreEqual(4096, MetadataCapacityProfile2.PageValues);
            Assert.AreEqual(524287, MetadataCapacityProfile2.UsablePages);
            Assert.AreEqual(393215, MetadataCapacityProfile2.MaxChargedPages);
            Assert.AreEqual(33554432UL, MetadataCapacityProfile2.MaxDllBytes);
            Assert.AreEqual(2, new MetadataCapacityProfile2PreliminaryReport().schemaVersion);
            Assert.AreEqual(2, new MetadataCapacityProfile2PreliminaryReport().nativeBudgetCapabilityVersion);
            Assert.IsNotNull(typeof(MetadataEncodingProfile2).GetField("invalidIndexSentinel"));
            Assert.IsNull(typeof(MetadataEncodingProfile2).GetField("aotSentinel"));
        }

        [Test]
        public void OrderedDllSizeBoundariesUseNativeFailurePriority()
        {
            var empty = MetadataCapacityPlannerProfile2.PreliminaryPlan(new[] { Input("empty", 0) }, 0);
            Assert.AreEqual("EmptyDll", empty.failureReason);
            Assert.AreEqual(0, empty.firstFailingIndex);
            Assert.AreEqual(0, empty.reservedImageCountAfter);
            Assert.AreEqual(0, empty.allocations.Length);

            var maximum = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                new[] { Input("maximum", MetadataCapacityProfile2.MaxDllBytes) }, 0);
            Assert.IsTrue(maximum.admissionAccepted);
            Assert.AreEqual(1, maximum.acceptedImages);
            Assert.AreEqual(1, maximum.allocations[0].imageId);
            Assert.AreEqual(-1, maximum.firstFailingIndex);

            var tooLarge = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                new[] { Input("too-large", MetadataCapacityProfile2.MaxDllBytes + 1) }, 0);
            Assert.AreEqual("DllTooLarge", tooLarge.failureReason);
            Assert.AreEqual(0, tooLarge.firstFailingIndex);
            Assert.AreEqual(0, tooLarge.reservedImageCountAfter);

            var invalidSizeStillPrecedesIdentity = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                new[] { new MetadataCapacityProfile2Input { name = "", dllSize = 0, sha256 = "bad" } }, 0);
            Assert.AreEqual("EmptyDll", invalidSizeStillPrecedesIdentity.failureReason);
        }

        [Test]
        public void ImageCountBoundariesAreAllOrNothingAndPreserveOrder()
        {
            var count8191 = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                Enumerable.Range(0, 8191).Select(i => Input("A" + i, 1)).ToArray(), 0);
            Assert.IsTrue(count8191.admissionAccepted);
            Assert.AreEqual(8191, count8191.reservedImageCountAfter);
            Assert.AreEqual(8191, count8191.allocations.Length);
            Assert.AreEqual(1, count8191.allocations[0].imageId);
            Assert.AreEqual(8191, count8191.allocations[8190].imageId);

            var count8192 = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                Enumerable.Range(0, 8192).Select(i => Input("B" + i, 1)).ToArray(), 0);
            Assert.IsTrue(count8192.admissionAccepted);
            Assert.AreEqual(8192, count8192.reservedImageCountAfter);

            var count8193 = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                Enumerable.Range(0, 8193).Select(i => Input("C" + i, 1)), 0);
            Assert.AreEqual("ImageLimit", count8193.failureReason);
            Assert.AreEqual(8192, count8193.firstFailingIndex);
            Assert.AreEqual(8193, count8193.inputs.Length);
            Assert.IsTrue(count8193.inputCountWasBounded);
            Assert.AreEqual(0, count8193.acceptedImages);
            Assert.AreEqual(0, count8193.reservedImageCountAfter);
            Assert.AreEqual(0, count8193.allocations.Length);
        }

        [Test]
        public void AlreadyReservedImageIdsRemainConsumedOnFailure()
        {
            var last = MetadataCapacityPlannerProfile2.PreliminaryPlan(new[] { Input("last", 1) }, 8191);
            Assert.IsTrue(last.admissionAccepted);
            Assert.AreEqual(8192, last.reservedImageCountAfter);
            Assert.AreEqual(8192, last.allocations[0].imageId);

            var beyondLast = MetadataCapacityPlannerProfile2.PreliminaryPlan(new[] { Input("beyond", 1) }, 8192);
            Assert.AreEqual("ImageLimit", beyondLast.failureReason);
            Assert.AreEqual(0, beyondLast.firstFailingIndex);
            Assert.AreEqual(8192, beyondLast.reservedImageCountBefore);
            Assert.AreEqual(8192, beyondLast.reservedImageCountAfter);
            Assert.AreEqual(0, beyondLast.allocations.Length);

            var overRemaining = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                new[] { Input("one", 1), Input("two", 1) }, 8191);
            Assert.AreEqual("ImageLimit", overRemaining.failureReason);
            Assert.AreEqual(1, overRemaining.firstFailingIndex);
            Assert.AreEqual(8191, overRemaining.reservedImageCountAfter);
        }

        [Test]
        public void AggregateEnvelopeIsInformationalAndFinalPageFitRemainsUnknown()
        {
            var report = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                Enumerable.Range(0, 17).Select(i => Input("D" + i, MetadataCapacityProfile2.MaxDllBytes)).ToArray(), 0);
            Assert.IsTrue(report.admissionAccepted);
            Assert.AreEqual(17UL * MetadataCapacityProfile2.MaxDllBytes, report.aggregateDllBytes);
            Assert.IsFalse(report.aggregateDllEnvelopeFits);
            Assert.IsTrue(report.aggregateDllEnvelopeExceeded);
            Assert.IsFalse(report.finalPageFitKnown);
            Assert.IsTrue(report.runtimeFinalizationRequired);
            Assert.AreEqual("Preliminary", report.admissionKind);
            Assert.IsTrue(report.fitsPreliminary);
            Assert.IsTrue(report.aggregateInputDllBytesInformational);
        }

        [Test]
        public void ArithmeticOverflowBoundaryAndInvalidStateFailClosedWithoutWrapping()
        {
            var nullInput = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                (System.Collections.Generic.IEnumerable<MetadataCapacityProfile2Input>)null, 0);
            Assert.AreEqual("InvalidInput", nullInput.failureReason);
            Assert.IsFalse(nullInput.admissionAccepted);

            var invalidState = MetadataCapacityPlannerProfile2.PreliminaryPlan(new[] { Input("ignored", 1) }, -1);
            Assert.AreEqual("InvalidState", invalidState.failureReason);
            Assert.AreEqual(0, invalidState.firstFailingIndex);
            Assert.AreEqual(-1, invalidState.reservedImageCountAfter);

            var maxUnsigned = MetadataCapacityPlannerProfile2.PreliminaryPlan(
                new[] { Input("unsigned-overflow-boundary", ulong.MaxValue) }, 0);
            Assert.AreEqual("DllTooLarge", maxUnsigned.failureReason);
            Assert.AreEqual(0UL, maxUnsigned.aggregateDllBytes);
            Assert.AreEqual(0, maxUnsigned.reservedImageCountAfter);
        }

        [Test]
        public void NativeProfile2CapacityJsonUsesSchemaTwoAndMinusOneSuccessSentinel()
        {
            string json = "{\"schemaVersion\":2,\"enabled\":true,\"profileVersion\":2,\"maximumImageCount\":8192,\"maximumDllBytes\":33554432,\"usablePageCapacity\":524287,\"chargedPageCeiling\":393215,\"minimumFreePageMargin\":131072,\"reservedPages\":0,\"mappedPages\":0,\"lifetimeReservedImageCount\":0,\"remainingImageCount\":8192,\"requiredImages\":0,\"acceptedImages\":0,\"firstFailingIndex\":-1,\"firstFailingSize\":0,\"failureReason\":\"None\",\"fitsPreliminary\":true,\"runtimeFinalizationRequired\":true,\"aggregateInputDllBytes\":0,\"aggregateInputDllBytesInformational\":true,\"ordinaryAllocatedCount\":0,\"shadowAllocatedCount\":0,\"reservedShadowImageCount\":0}";
            HybridCLR.AssemblyShadowMetadataCapacityProfile2 report;
            Assert.IsTrue(HybridCLR.AssemblyShadowMetadataCapacityProfile2.TryParse(json, out report));
            Assert.AreEqual(2, report.schemaVersion);
            Assert.AreEqual(-1, report.firstFailingIndex);
            Assert.IsTrue(report.runtimeFinalizationRequired);
            Assert.IsFalse(HybridCLR.AssemblyShadowMetadataCapacityProfile2.TryParse(json.Replace("\"firstFailingIndex\":-1", "\"firstFailingIndex\":0"), out report));
            Assert.IsFalse(HybridCLR.AssemblyShadowMetadataCapacityProfile2.TryParse(
                json.Replace("\"reservedPages\":0", "\"reservedPages\":400000"), out report));
            Assert.IsFalse(HybridCLR.AssemblyShadowMetadataCapacityProfile2.TryParse(
                json.Replace("\"lifetimeReservedImageCount\":0,\"remainingImageCount\":8192,\"requiredImages\":0,\"acceptedImages\":0",
                    "\"lifetimeReservedImageCount\":8192,\"remainingImageCount\":0,\"requiredImages\":1,\"acceptedImages\":1"), out report));

            string imageLimit = json.Replace("\"requiredImages\":0", "\"requiredImages\":8193")
                .Replace("\"firstFailingIndex\":-1", "\"firstFailingIndex\":8192")
                .Replace("\"firstFailingSize\":0", "\"firstFailingSize\":1")
                .Replace("\"failureReason\":\"None\"", "\"failureReason\":\"ImageLimit\"")
                .Replace("\"fitsPreliminary\":true", "\"fitsPreliminary\":false");
            Assert.IsTrue(HybridCLR.AssemblyShadowMetadataCapacityProfile2.TryParse(imageLimit, out report));
            Assert.AreEqual(8193UL, report.requiredImages);
            Assert.AreEqual(0UL, report.acceptedImages);
            Assert.AreEqual(8192, report.firstFailingIndex);
        }

        [Test]
        public void RuntimeTransactionAndReservationDefaultsUseProfile2()
        {
            var begin = typeof(HybridCLR.AssemblyShadowRuntime).GetMethod("BeginTransaction").GetParameters()[3];
            var reserve = typeof(HybridCLR.AssemblyShadowRuntime).GetMethod("ReserveMetadataBudget").GetParameters()[1];
            Assert.AreEqual(2, begin.DefaultValue);
            Assert.AreEqual(2, reserve.DefaultValue);
        }
    }
}
