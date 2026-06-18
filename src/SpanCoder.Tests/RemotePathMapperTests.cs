using System;
using Xunit;
using SpanCoder.App;

namespace SpanCoder.Tests
{
    public class RemotePathMapperTests
    {
        [Fact]
        public void TestNoMappingSpec()
        {
            var mapper = new RemotePathMapper(null);
            
            string localPath = @"C:\Users\spuri\src\file.cs";
            string remotePath = "/remote/src/file.cs";
            
            Assert.Equal(localPath, mapper.ToRemote(localPath));
            Assert.Equal(remotePath, mapper.ToLocal(remotePath));
            
            string frame = "MyMethod(/remote/src/file.cs:12)";
            Assert.Equal(frame, mapper.TranslateFrame(frame));
        }

        [Fact]
        public void TestValidMappingToRemote()
        {
            var mapper = new RemotePathMapper(@"C:\local\src=/remote/src");
            
            string localPath = @"C:\local\src\subdir\file.cs";
            string expectedRemote = "/remote/src/subdir/file.cs";
            
            Assert.Equal(expectedRemote, mapper.ToRemote(localPath));
        }

        [Fact]
        public void TestValidMappingToLocal()
        {
            var mapper = new RemotePathMapper(@"C:\local\src=/remote/src");
            
            string remotePath = "/remote/src/subdir/file.cs";
            string expectedLocal = @"C:\local\src\subdir\file.cs";
            
            Assert.Equal(expectedLocal, mapper.ToLocal(remotePath));
        }

        [Fact]
        public void TestUnmatchedPaths()
        {
            var mapper = new RemotePathMapper(@"C:\local\src=/remote/src");
            
            string unmatchedLocal = @"C:\other\src\file.cs";
            string unmatchedRemote = "/other/src/file.cs";
            
            Assert.Equal(unmatchedLocal, mapper.ToRemote(unmatchedLocal));
            Assert.Equal(unmatchedRemote, mapper.ToLocal(unmatchedRemote));
        }

        [Fact]
        public void TestTranslateFrame()
        {
            var mapper = new RemotePathMapper(@"C:\local\src=/remote/src");
            
            string frame = "MyMethod(/remote/src/subdir/file.cs:12)";
            string expectedFrame = @"MyMethod(C:\local\src\subdir\file.cs:12)";
            
            Assert.Equal(expectedFrame, mapper.TranslateFrame(frame));
        }

        [Fact]
        public void TestTranslateFrameUnmatched()
        {
            var mapper = new RemotePathMapper(@"C:\local\src=/remote/src");
            
            string frame = "MyMethod(/other/src/file.cs:12)";
            Assert.Equal(frame, mapper.TranslateFrame(frame));
        }
    }
}
