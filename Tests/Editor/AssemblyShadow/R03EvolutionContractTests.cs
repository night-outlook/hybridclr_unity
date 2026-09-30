using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R03EvolutionContractTests
    {
        public static string[] Cases { get { return R03EvolutionContractCases.CaseIds; } }

        [TestCaseSource(nameof(Cases))]
        public void RealDllEvolutionContract(string id)
        {
            R03EvolutionContractCases.Run(id);
        }
    }
}
