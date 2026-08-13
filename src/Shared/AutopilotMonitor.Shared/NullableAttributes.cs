// netstandard2.0 ships none of the nullable-flow attributes, so shared Try-pattern APIs
// could not tell nullable-enabled consumers (backend net10, agent test projects) that an
// out param is non-null on success. Internal polyfill — the consuming compiler matches
// these attributes by full name from metadata, so accessibility does not matter.
#if NETSTANDARD2_0
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }
}
#endif
