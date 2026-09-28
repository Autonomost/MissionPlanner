using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.Utilities;
using System;
using System.IO;
using System.Linq;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Regression tests for the POI list piling duplicates on top of each other: the saved file
    /// was loaded once per page that subscribed to POIModified, and every save then made the
    /// duplicates permanent, doubling the markers on each start.
    /// </summary>
    [TestClass]
    public class POITests
    {
        private string _dir;
        private string _file;

        private const string Airfield = "53.7299529815367\t33.4349584579468\tAirfield\r\n";
        private const string Launch = "52.2171684776927\t33.2859188318253\tLaunch\r\n";

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mp-poi-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "poi.txt");
            POI.ResetForTests();
            POI.FileName = _file;
        }

        [TestCleanup]
        public void Cleanup()
        {
            POI.ResetForTests();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string[] Lines(string file) =>
            File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

        [TestMethod]
        public void LoadFile_TwiceFromSameFile_DoesNotDuplicate()
        {
            File.WriteAllText(_file, Airfield + Launch);

            POI.LoadFile(_file);
            POI.LoadFile(_file);

            Assert.AreEqual(2, POI.Count);
            Assert.AreEqual(2, Lines(_file).Length, "a clean file must not be rewritten with duplicates");
        }

        [TestMethod]
        public void LoadFile_FileWithDuplicates_CollapsesAndRewritesFile()
        {
            // what an affected install looks like after a few starts
            File.WriteAllText(_file, Airfield + Launch + Airfield + Launch + Airfield + Launch);

            POI.LoadFile(_file);

            Assert.AreEqual(2, POI.Count);
            var lines = Lines(_file);
            Assert.AreEqual(2, lines.Length, "the saved file should be rewritten without the duplicates");
            CollectionAssert.AreEquivalent(new[] { Airfield.TrimEnd(), Launch.TrimEnd() }, lines);
        }

        [TestMethod]
        public void POIModified_SubscribedByTwoPages_LoadsFileOnce()
        {
            File.WriteAllText(_file, Airfield + Launch);
            EventHandler dataPage = (s, e) => { };
            EventHandler planPage = (s, e) => { };

            try
            {
                POI.POIModified += dataPage;
                POI.POIModified += planPage;

                Assert.AreEqual(2, POI.Count);
            }
            finally
            {
                POI.POIModified -= dataPage;
                POI.POIModified -= planPage;
            }
        }

        [TestMethod]
        public void LoadFile_SamePositionDifferentName_KeepsBoth()
        {
            File.WriteAllText(_file,
                "53.7\t33.4\tAlpha\r\n" +
                "53.7\t33.4\tBravo\r\n");

            POI.LoadFile(_file);

            Assert.AreEqual(2, POI.Count);
        }

        [TestMethod]
        public void LoadFile_MalformedLine_IsIgnored()
        {
            File.WriteAllText(_file, "not\ta\tnumber\r\n" + Airfield);

            POI.LoadFile(_file);

            Assert.AreEqual(1, POI.Count);
        }
    }
}
