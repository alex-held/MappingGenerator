using System;
using System.Collections.Generic;
using System.Diagnostics;
using CodeGeneration.Roslyn;

namespace OnBuildGenerator
{
    [AttributeUsage(AttributeTargets.Interface)]
    [CodeGenerationAttribute(typeof(OnBuildMappingGenerator))]
    [Conditional("CodeGeneration")]
    public class MappingInterface:Attribute
    {
        
    }
}
