using System;
using System.Linq;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TemplateTests
    {
        #region Constructor

        [Test]
        public void Template_Named_SetsName()
        {
            var t = new Template(
                debugName: "MyTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual("MyTemplate", t.DebugName);
        }

        [Test]
        public void Template_WithTags_SetsTags()
        {
            var t = new Template(
                debugName: "TaggedTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: new Tag[] { TestTags.Alpha, TestTags.Beta }
            );

            NAssert.AreEqual(2, t.LocalTags.Count);
            NAssert.IsTrue(t.LocalTags.Contains(TestTags.Alpha));
            NAssert.IsTrue(t.LocalTags.Contains(TestTags.Beta));
        }

        [Test]
        public void Template_Component_AddsDeclaration()
        {
            var t = new Template(
                debugName: "OneComp",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual(1, t.LocalComponentDeclarations.Count);
        }

        [Test]
        public void Template_MultipleComponents_AddsAll()
        {
            var t = new Template(
                debugName: "MultiComp",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(null, null, null, null, null, null, null),
                    new ComponentDeclaration<TestFloat>(null, null, null, null, null, null, null),
                    new ComponentDeclaration<TestVec>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual(3, t.LocalComponentDeclarations.Count);
        }

        [Test]
        public void Template_Partitions_SetsPartitions()
        {
            var t = new Template(
                debugName: "StatefulTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: new TagSet[]
                {
                    TagSet.FromTags(TestTags.PartitionA),
                    TagSet.FromTags(TestTags.PartitionB),
                },
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Gamma }
            );

            NAssert.AreEqual(2, t.Partitions.Count);
        }

        #endregion

        #region Inheritance

        [Test]
        public void Template_InheritsFrom_HasParentComponents()
        {
            var parent = new Template(
                debugName: "Parent",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            var child = new Template(
                debugName: "Child",
                localBaseTemplates: new Template[] { parent },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: new Tag[] { TestTags.Beta }
            );

            NAssert.AreEqual(1, child.LocalBaseTemplates.Count);
            NAssert.AreSame(parent, child.LocalBaseTemplates[0]);
        }

        [Test]
        public void Template_InheritsFrom_CanAddOwn()
        {
            var parent = new Template(
                debugName: "ParentForChild",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            var child = new Template(
                debugName: "ChildWithOwn",
                localBaseTemplates: new Template[] { parent },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestFloat>(null, null, null, null, null, null, null),
                },
                localTags: new Tag[] { TestTags.Beta }
            );

            // Child has its own local component plus base template
            NAssert.AreEqual(1, child.LocalComponentDeclarations.Count);
            NAssert.AreEqual(1, child.LocalBaseTemplates.Count);
        }

        #endregion

        #region Defaults

        [Test]
        public void Template_WithDefault_StoresValue()
        {
            var t = new Template(
                debugName: "DefaultTest",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        variableUpdateOnly: null,
                        isInput: null,
                        inputFrameBehaviour: null,
                        warnOnMissingInput: null,
                        isConstant: null,
                        isInterpolated: null,
                        defaultValue: new TestInt { Value = 42 }
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual(1, t.LocalComponentDeclarations.Count);
            var decl = t.LocalComponentDeclarations[0];
            NAssert.IsTrue(decl.HasDefault, "Declaration should have a default value");
            var typed = (ComponentDeclaration<TestInt>)decl;
            NAssert.IsNotNull(typed.Default);
            NAssert.AreEqual(42, typed.Default.Value.Value, "Default value should be 42");
        }

        [Test]
        public void Template_IsInterpolated_SetsFlag()
        {
            var t = new Template(
                debugName: "InterpTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        variableUpdateOnly: null,
                        isInput: null,
                        inputFrameBehaviour: null,
                        warnOnMissingInput: null,
                        isConstant: null,
                        isInterpolated: true,
                        defaultValue: null
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual(1, t.LocalComponentDeclarations.Count);
            var typed = (ComponentDeclaration<TestInt>)t.LocalComponentDeclarations[0];
            NAssert.AreEqual(true, typed.IsInterpolated, "IsInterpolated flag should be true");
            NAssert.IsNull(typed.IsInput, "IsInput should not be set");
        }

        [Test]
        public void Template_IsInput_SetsFlag()
        {
            var t = new Template(
                debugName: "InputTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        variableUpdateOnly: null,
                        isInput: true,
                        inputFrameBehaviour: MissingInputBehavior.ResetToDefault,
                        warnOnMissingInput: false,
                        isConstant: null,
                        isInterpolated: null,
                        defaultValue: null
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

            NAssert.AreEqual(1, t.LocalComponentDeclarations.Count);
            var decl = t.LocalComponentDeclarations[0];
            NAssert.AreEqual(true, decl.IsInput, "IsInput flag should be true");
            NAssert.AreEqual(MissingInputBehavior.ResetToDefault, decl.MissingInputBehavior);
            NAssert.AreEqual(false, decl.WarnOnMissingInput);
        }

        #endregion
    }
}
