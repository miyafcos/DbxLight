namespace DbxLight.Tests;

internal static partial class Unit5dTests
{
    internal static void Run(TestRunner runner, string? only = null, bool includeMounts = true)
    {
        if (only == "1-failure") { runner.Test("S1: deadline preserves confirmed results when an unconsumed read fails", TimedReadFailure); return; }
        if (only is null || only == "1")
        {
            runner.Test("S1: time-limit validation and atomic one-folder commands retain their contracts", TimeLimitOptions);
            runner.Test("S1: empty timed search retains continuation and drains readers", EmptyTimedSearch);
            runner.Test("S1: count and scan stops keep timeLimited false", CountLimitReason);
            runner.Test("S1: deadline preserves confirmed results when an unconsumed read fails", TimedReadFailure);
            runner.Test("S1: time continuation guidance stays within forty lines", TimeLimitGuide);
            if (includeMounts)
            {
                runner.Test("S1: timed listing pages equal one complete ordered local listing", TimedListingPages);
                runner.Test("S1: timed find pages equal one complete ordered local search", TimedFindPages);
            }
        }
        if ((only is null || only == "2") && includeMounts)
            runner.Test("S2: missing ls, find and refresh paths return not_found while hidden stays refused", MissingPaths);
        if (only is null || only == "2")
            runner.Test("S2: stopped local ls and find classify absent directories", StoppedMissingPaths);
        if (only == "4") runner.Test("filter: sample all 26 rules", UnitTests.SampleFilter);
    }
}
