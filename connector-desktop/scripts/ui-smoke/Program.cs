namespace Connector.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "webview")
            return GraphiteWebSmoke.Run(args[1]);
        if (args.Length is 3 or 4 && args[0] == "window")
            return args.Length == 4 && args[3] == "min" ? WindowAcceptance.Run(args[1], args[2], 1, minimumSize: true)
                : WindowAcceptance.Run(args[1], args[2], args.Length == 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1);
        if (args.Length == 5 && args[0] == "engines")
            return EngineSmoke.RunAsync(args[1], args[2], args[3], args[4]).GetAwaiter().GetResult();
        throw new ArgumentException("Usage: UiSmoke webview <output>; window <output> <states.json> [scale|min]; engines <FBX part> <gltfpack> <IFC worker> <output>.");
    }
}