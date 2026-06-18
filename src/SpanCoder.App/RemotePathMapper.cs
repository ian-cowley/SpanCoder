using System;

namespace SpanCoder.App
{
    public class RemotePathMapper
    {
        private readonly string _localPrefix;
        private readonly string _remotePrefix;
        private readonly bool _hasMapping;

        public RemotePathMapper(string? mappingSpec)
        {
            if (string.IsNullOrEmpty(mappingSpec))
            {
                _localPrefix = "";
                _remotePrefix = "";
                _hasMapping = false;
                return;
            }

            int idx = mappingSpec.IndexOf('=');
            if (idx > 0)
            {
                _localPrefix = mappingSpec.Substring(0, idx).Trim();
                _remotePrefix = mappingSpec.Substring(idx + 1).Trim();
                _hasMapping = true;
            }
            else
            {
                _localPrefix = "";
                _remotePrefix = "";
                _hasMapping = false;
            }
        }

        public string ToRemote(string localPath)
        {
            if (!_hasMapping || string.IsNullOrEmpty(localPath)) return localPath;
            if (localPath.StartsWith(_localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = localPath.Substring(_localPrefix.Length);
                relative = relative.Replace('\\', '/'); // Remote is usually Unix/Linux
                return _remotePrefix + relative;
            }
            return localPath;
        }

        public string ToLocal(string remotePath)
        {
            if (!_hasMapping || string.IsNullOrEmpty(remotePath)) return remotePath;
            if (remotePath.StartsWith(_remotePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = remotePath.Substring(_remotePrefix.Length);
                relative = relative.Replace('/', '\\'); // Local is Windows
                return _localPrefix + relative;
            }
            return remotePath;
        }

        public string TranslateFrame(string frame)
        {
            if (!_hasMapping || string.IsNullOrEmpty(frame)) return frame;
            int idx = frame.IndexOf('(');
            if (idx >= 0)
            {
                string prefix = frame.Substring(0, idx + 1);
                string pathPart = frame.Substring(idx + 1);
                if (pathPart.StartsWith(_remotePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = pathPart.Substring(_remotePrefix.Length);
                    relative = relative.Replace('/', '\\');
                    return prefix + _localPrefix + relative;
                }
            }
            return frame;
        }
    }
}
