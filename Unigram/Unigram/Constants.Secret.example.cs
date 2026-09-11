// Copy this file to Constants.Secret.cs (gitignored) and fill in your own values.
// Get ApiId/ApiHash by registering an app at https://my.telegram.org ("API development tools").
// The build fails without Constants.Secret.cs; with ApiId = 0 it compiles but cannot log in.
namespace Unigram
{
    public static partial class Constants
    {
        static Constants()
        {
            ApiId = 0;
            ApiHash = "";

            AppChannel = "sideload";
        }
    }
}
