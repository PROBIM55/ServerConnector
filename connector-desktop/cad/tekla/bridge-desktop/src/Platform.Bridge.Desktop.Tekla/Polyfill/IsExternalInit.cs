// Polyfill для C# init-only accessors и positional records на net48.
// .NET Framework не поставляет IsExternalInit; компилятор требует его
// присутствия в любой сборке использующей init / record. Объявляем
// самостоятельно — стандартная техника.

using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
