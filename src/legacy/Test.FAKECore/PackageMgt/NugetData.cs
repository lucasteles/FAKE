namespace Test.FAKECore.PackageMgt
{
    public class NugetData
    {
        public static string RepositoryUrl
        {
            get { return "https://www.nuget.org/v1/FeedService.svc"; }
        }

        public static string OutputDir
        {
            get { return @"output\"; }
        }
    }
}