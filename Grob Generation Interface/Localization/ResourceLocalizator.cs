using System.Globalization;
using FS.SDK.Localization;


namespace GrobGenerationInterface.Localization
{
    public class ResourceLocalizator : IResourceLocalizator
    {
        private const string BaseIconUriPath = "pack://application:,,,/GrobGenerationInterface;Component/Content/Icons";
        private const string IconUriSuffix = ".svg";

        /// <summary>
        ///     Gets string accesor.
        /// </summary>
        /// <param name="resourceKey">  The resource key. </param>
        /// <returns>
        ///     The string accesor.
        /// </returns>
        public string GetString(string resourceKey)
        {
            return Resources.ResourceManager.GetString(resourceKey);
        }

        /// <summary>
        ///     Gets string accesor.
        /// </summary>
        /// <param name="resourceKey"></param>
        /// <param name="culture">  The culture. </param>
        /// <returns>
        ///     The string accesor.
        /// </returns>
        public string GetString(string resourceKey, CultureInfo culture)
        {
            return Resources.ResourceManager.GetString(resourceKey, culture);
        }

        public string GetImageUriString(string resourceKey)
        {
            return $"{BaseIconUriPath}/{resourceKey}{IconUriSuffix}";
        }
    }
}