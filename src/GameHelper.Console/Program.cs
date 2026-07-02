using GameHelper.Core;
using WarzoneHelper.Game;

namespace GameHelper.ConsoleHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // All logic lives in Core so it can also be hosted by a DLL runner. The game plug-in
            // selects which game we monitor — swap WarzoneProfile for another game's profile here.
            return ConsoleRunner.Run(new WarzoneProfile(), args);
        }
    }
}
