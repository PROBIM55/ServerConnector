namespace Connector.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "webview")
            return GraphiteWebSmoke.Run(args[1]);
        if (args.Length == 5 && args[0] == "webview-engines")
            return GraphiteWebSmoke.Run(args[4], args[1], args[2], args[3]);
        if (args.Length == 5 && args[0] == "engines")
            return EngineSmoke.RunAsync(args[1], args[2], args[3], args[4]).GetAwaiter().GetResult();
        throw new ArgumentException("Usage: UiSmoke webview <output>; engines|webview-engines <FBX part> <gltfpack> <IFC worker> <output>.");
    }
}