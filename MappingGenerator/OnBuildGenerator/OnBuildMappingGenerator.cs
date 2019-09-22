using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodeGeneration.Roslyn;
using MappingGenerator.Features.Refactorings.Mapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace OnBuildGenerator
{
    public static class References
    {
        public static readonly MetadataReference Core = References.FromType<int>();
        public static readonly MetadataReference Linq = References.FromType(typeof(Enumerable));
        public static readonly MetadataReference NetStandardCore = (MetadataReference)MetadataReference.CreateFromFile(((IEnumerable<string>)((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)).FirstOrDefault<string>((Func<string, bool>)(x => x.EndsWith("mscorlib.dll"))), new MetadataReferenceProperties(), (DocumentationProvider)null);

        public static MetadataReference FromType<T>()
        {
            return References.FromType(typeof(T));
        }

        public static MetadataReference FromType(Type type)
        {
            return (MetadataReference)MetadataReference.CreateFromFile(type.Assembly.Location, new MetadataReferenceProperties(), (DocumentationProvider)null);
        }
    }

    public class OnBuildMappingGenerator: IRichCodeGenerator
    {
        private const string GeneratorName = "MappingGenerator.OnBuildMappingGenerator";
        private static readonly MappingImplementorEngine ImplementorEngine = new MappingImplementorEngine();
        private static readonly Lazy<string> GeneratorVersion = new  Lazy<string>(()=> Assembly.GetExecutingAssembly().GetName().Version.ToString());
        public OnBuildMappingGenerator(AttributeData attributeData)
        {
        }

        Task<SyntaxList<MemberDeclarationSyntax>> ICodeGenerator.GenerateAsync(TransformationContext context, IProgress<Diagnostic> progress, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<RichGenerationResult> GenerateRichAsync(TransformationContext context, IProgress<Diagnostic> progress, CancellationToken cancellationToken)
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
                .AddMetadataReferences(context.Compilation.References
                    .Append(References.Core)
                    .Append(References.Linq)
                    .Append(References.NetStandardCore)
                );
            var document = project.AddDocument("TestDocument", "", (IEnumerable<string>)null, (string)null);
            var syntaxGenerator = SyntaxGenerator.GetGenerator(document);
            var results = SyntaxFactory.List<MemberDeclarationSyntax>(new List<MemberDeclarationSyntax>()
            {

            });

            var mappingDeclaration = (InterfaceDeclarationSyntax)context.ProcessingNode;

            var mappingClass = (ClassDeclarationSyntax)syntaxGenerator.ClassDeclaration(
                mappingDeclaration.Identifier.Text.Substring(1),
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.Partial,
                interfaceTypes: new List<SyntaxNode>()
                {
                    syntaxGenerator.TypeExpression(context.SemanticModel.GetDeclaredSymbol(mappingDeclaration))
                },
                members: mappingDeclaration.Members.Select(x =>
                {
                    if (x is MethodDeclarationSyntax methodDeclaration)
                    {
                        var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDeclaration);
                        var statements = ImplementorEngine.CanProvideMappingImplementationFor(methodSymbol) ? ImplementorEngine.GenerateMappingStatements(methodSymbol, syntaxGenerator, context.SemanticModel) :
                                new List<StatementSyntax>()
                                {
                                    GenerateThrowNotSupportedException(context, syntaxGenerator, methodSymbol.Name)
                                };

                        return ((MethodDeclarationSyntax)syntaxGenerator.MethodDeclaration(
                            methodDeclaration.Identifier.Text,
                            parameters: methodDeclaration.ParameterList.Parameters,
                            accessibility: Accessibility.Public,
                            typeParameters: methodDeclaration.TypeParameterList?.Parameters.Select(xx => xx.Identifier.Text),
                            returnType: methodDeclaration.ReturnType
                        )).WithBody(SyntaxFactory.Block(statements));
                        
                    }

                    return x;
                }));
            mappingClass = DecorateWithGeneratedCodeAttribute(syntaxGenerator, mappingClass);
            results = results.Add(
                mappingClass
                    .WithAdditionalAnnotations(Simplifier.Annotation)
                    .WithAdditionalAnnotations(Formatter.Annotation)
                );
            var newRoot = context.ProcessingNode.Ancestors().Aggregate(results, WrapInAncestor);
            return Task.FromResult(new RichGenerationResult()
            {
                Members = newRoot,
                Usings = new SyntaxList<UsingDirectiveSyntax>(new []
                {
                    SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Linq"))
                })
            });
        }

        private static StatementSyntax GenerateThrowNotSupportedException(TransformationContext context, SyntaxGenerator syntaxGenerator, string methodName)
        {
            var notImplementedExceptionType = context.SemanticModel.Compilation.GetTypeByMetadataName("System.NotSupportedException");
            var createNotImplementedException = syntaxGenerator.ObjectCreationExpression(notImplementedExceptionType,
                
                syntaxGenerator.LiteralExpression($"'{methodName}' method signature is not supported by {GeneratorName}"));
            return (StatementSyntax)syntaxGenerator.ThrowStatement(createNotImplementedException);
        }

        private static ClassDeclarationSyntax DecorateWithGeneratedCodeAttribute(SyntaxGenerator syntaxGenerator,
            ClassDeclarationSyntax mappingClass)
        {
            var generatedCodeAttribute = syntaxGenerator.Attribute("System.CodeDom.Compiler.GeneratedCodeAttribute",
                syntaxGenerator.LiteralExpression(GeneratorName),
                syntaxGenerator.LiteralExpression(GeneratorVersion)
            );
            mappingClass = (ClassDeclarationSyntax) syntaxGenerator.AddAttributes(mappingClass, generatedCodeAttribute);
            return mappingClass;
        }

        private static SyntaxList<MemberDeclarationSyntax> WrapInAncestor(SyntaxList<MemberDeclarationSyntax> generatedMembers, SyntaxNode ancestor)
        {
            switch (ancestor)
            {
                case NamespaceDeclarationSyntax ancestorNamespace:
                    generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                        CopyAsAncestor(ancestorNamespace)
                        .WithMembers(generatedMembers));
                    break;
                case ClassDeclarationSyntax nestingClass:
                    generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                        CopyAsAncestor(nestingClass)
                        .WithMembers(generatedMembers));
                    break;
                case StructDeclarationSyntax nestingStruct:
                    generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                        CopyAsAncestor(nestingStruct)
                        .WithMembers(generatedMembers));
                    break;
            }
            return generatedMembers;
        }

        private static NamespaceDeclarationSyntax CopyAsAncestor(NamespaceDeclarationSyntax syntax)
        {
            return SyntaxFactory.NamespaceDeclaration(syntax.Name.WithoutTrivia())
                .WithExterns(SyntaxFactory.List(syntax.Externs.Select(x => x.WithoutTrivia())))
                .WithUsings(SyntaxFactory.List(syntax.Usings.Select(x => x.WithoutTrivia())))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static ClassDeclarationSyntax CopyAsAncestor(ClassDeclarationSyntax syntax)
        {
            return SyntaxFactory.ClassDeclaration(syntax.Identifier.WithoutTrivia())
                .WithModifiers(SyntaxFactory.TokenList(syntax.Modifiers.Select(x => x.WithoutTrivia())))
                .WithTypeParameterList(syntax.TypeParameterList)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static StructDeclarationSyntax CopyAsAncestor(StructDeclarationSyntax syntax)
        {
            return SyntaxFactory.StructDeclaration(syntax.Identifier.WithoutTrivia())
                .WithModifiers(SyntaxFactory.TokenList(syntax.Modifiers.Select(x => x.WithoutTrivia())))
                .WithTypeParameterList(syntax.TypeParameterList)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
    }
}