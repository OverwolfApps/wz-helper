namespace WarzoneHelper.ConsoleHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // All logic lives in Core so it can also be hosted by a DLL runner.
            return WarzoneHelper.Core.ConsoleRunner.Run(args);
        }
    }
}
