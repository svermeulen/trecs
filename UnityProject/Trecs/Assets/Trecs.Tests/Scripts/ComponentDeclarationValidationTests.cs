using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public partial struct ConflictTestComp : IEntityComponent
    {
        public int Value;
    }

    [TestFixture]
    public class ComponentDeclarationValidationTests
    {
        #region Conflicting FixedUpdateOnly

        [Test]
        public void ComponentDeclaration_ConflictingFixedUpdateOnly_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                fixedUpdateOnly: true,
                null,
                null,
                null,
                null,
                null,
                null,
                null
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                fixedUpdateOnly: false,
                null,
                null,
                null,
                null,
                null,
                null,
                null
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region Conflicting IsInput

        [Test]
        public void ComponentDeclaration_ConflictingIsInput_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                isInput: true,
                MissingInputFrameBehaviour.ResetToDefault,
                null,
                null,
                null,
                null
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                isInput: false,
                null,
                null,
                null,
                null,
                null
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region Conflicting IsConstant

        [Test]
        public void ComponentDeclaration_ConflictingIsConstant_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                isConstant: true,
                null,
                null
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                isConstant: false,
                null,
                null
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region FixedUpdateOnly + VariableUpdateOnly Conflict

        [Test]
        public void ComponentDeclaration_FixedAndVariable_Throws()
        {
            var dec = new ComponentDeclaration<ConflictTestComp>(
                fixedUpdateOnly: true,
                variableUpdateOnly: true,
                null,
                null,
                null,
                null,
                null,
                null
            );

            NAssert.Catch(() =>
            {
                dec.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region Interpolated + Constant Conflict

        [Test]
        public void ComponentDeclaration_InterpolatedAndConstant_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                isConstant: true,
                isInterpolated: null,
                null
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                isConstant: null,
                isInterpolated: true,
                null
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region Input + Constant Conflict

        [Test]
        public void ComponentDeclaration_InputAndConstant_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                isInput: true,
                MissingInputFrameBehaviour.ResetToDefault,
                null,
                null,
                null,
                null
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                isConstant: true,
                null,
                null
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion

        #region Matching Declarations Merge Cleanly

        [Test]
        public void ComponentDeclaration_CompatibleMerge_Succeeds()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                fixedUpdateOnly: true,
                null,
                null,
                null,
                null,
                null,
                null,
                new ConflictTestComp { Value = 42 }
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                fixedUpdateOnly: true,
                null,
                null,
                null,
                null,
                null,
                null,
                new ConflictTestComp { Value = 42 }
            );

            var result = dec1.MergeAsConcrete(
                new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                "TestTemplate"
            );

            NAssert.IsNotNull(result);
            NAssert.Greater(result.Count, 0);
        }

        #endregion

        #region Conflicting Default Values

        [Test]
        public void ComponentDeclaration_ConflictingDefaults_Throws()
        {
            var dec1 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new ConflictTestComp { Value = 10 }
            );
            var dec2 = new ComponentDeclaration<ConflictTestComp>(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new ConflictTestComp { Value = 20 }
            );

            NAssert.Catch(() =>
            {
                dec1.MergeAsConcrete(
                    new System.Collections.Generic.List<IComponentDeclaration> { dec1, dec2 },
                    "TestTemplate"
                );
            });
        }

        #endregion
    }
}
