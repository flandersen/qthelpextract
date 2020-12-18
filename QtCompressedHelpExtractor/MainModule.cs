using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic;

namespace QtCompressedHelpExtractor
{
    static class MainModule
    {
        private static string _contentFilePath;
        private static string _coverFile;
        private static string _inputFilePath;
        private static string _outputPath;
        private static string _wkhtmltopdf;

        public static int Main()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            if (Environment.GetCommandLineArgs().Length < 3)
            {
                Console.WriteLine("{0} <sqlite_file_path> <output_directory>", Process.GetCurrentProcess().ProcessName);
                return 1;
            }

            _inputFilePath = Environment.GetCommandLineArgs()[1];
            _outputPath = Environment.GetCommandLineArgs()[2];

            if (string.IsNullOrEmpty(_inputFilePath) || !File.Exists(_inputFilePath))
            {
                Console.WriteLine("Error: SQLite file does not exist.");
                Console.ReadKey();
                return 2;
            }

            try
            {
                // Check if target path is valid.
                new DirectoryInfo(_outputPath);
            }
            catch
            {
                Console.WriteLine("Error: Incorrect target folder.");
                Console.ReadKey();
                return 2;
            }

            try
            {
                Console.WriteLine("Starting extraction ...");
                _contentFilePath = Path.Combine(_outputPath, "content.txt");
                ExportFileOrder();
                ExtractFilesFromSqlite();
                Console.WriteLine("Finished.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unknown error: {0}{1}{2}", ex.Message, Constants.vbCrLf, ex.StackTrace);
                return 1;
            }
        }

        private static void CreatePdf()
        {
            var content = string.Empty;
            var args = "--footer-center [page] /[topage] " +
                "--enable-local-file-access " +
                "--load-error-handling skip " +
                "--read-args-from-stdin " +
                "--image-dpi 300 " +
                "--image-quality 80 " +
                "cover {0}";

            args = string.Format(args, _coverFile);

            using (var reader = new StreamReader(_contentFilePath))
            {
                if (!reader.EndOfStream)
                {
                    content = reader.ReadLine();
                }                
            }

            var process = new Process { StartInfo = { FileName = _wkhtmltopdf, Arguments = args } };
            process.StandardInput.WriteLine(content);
            process.Start();
        }

        private static void ExportFileOrder()
        {
            var data = GetTableOfContentAsBlob();
            var filePaths = ExtractFilePathsFromTableOfContent(data);
            var pathList = RemoveAnchorLinks(filePaths.Keys.ToList());
            var joinedPaths = string.Join(" ", pathList);

            using (var writer = new StreamWriter(_contentFilePath))
            {
                writer.WriteLine(joinedPaths);
            }
        }

        private static IList<string> RemoveAnchorLinks(IList<string> filePaths)
        {
            var withoutAnchors = new List<string>();
            var regex = new System.Text.RegularExpressions.Regex(@"#{1}\w+");

            foreach (string path in filePaths)
            {
                var withoutAnchor = regex.Replace(path, string.Empty);
                withoutAnchors.Add(withoutAnchor);
            }

            return withoutAnchors;
        }

        private static Dictionary<string, string> ExtractFilePathsFromTableOfContent(byte[] data)
        {
            var filePaths = new Dictionary<string, string>();
            var lastIndex = data.Length - 1;
            var i = 0;

            while (i <= lastIndex)
            {
                int filePathLength;
                int level;
                int titleLength;

                string filePath;
                string title;

                i += 2; // skip null-bytes
                level = data[i + 1] + 256 * data[i]; // big endian
                i += 2;

                if (data[i] == 0xFF && data[i + 1] == 0xFF)
                {
                    // chapter has no file...
                    filePath = string.Empty;
                    i += 4; // consume four bytes
                }
                else
                {
                    // chapter has file...
                    i += 2; // skip null-bytes
                    filePathLength = data[i + 1] + 256 * data[i]; // big endian
                    i += 2;
                    filePath = Encoding.BigEndianUnicode.GetString(data, i, filePathLength);
                    i += filePathLength;
                }

                i += 2; // skip null-bytes
                titleLength = data[i + 1] + 256 * data[i]; // big endian
                i += 2;
                title = Encoding.BigEndianUnicode.GetString(data, i, titleLength);
                i += titleLength;

                filePaths[filePath] = title;
            }

            return filePaths;
        }

        private static byte[] GetTableOfContentAsBlob()
        {
            byte[] data = null;
            using (var dbConn = new SQLiteConnection(string.Format("DataSource=\"{0}\"", _inputFilePath)))
            {
                dbConn.Open();
                var filePaths = new Dictionary<string, string>();

                using (var cmd = dbConn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT Data from ContentsTable " +
                        "WHERE ID = 1";

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            data = (byte[])reader.GetValue(0);
                        }
                    }
                }
            }

            return data;
        }

        private static void ExtractFilesFromSqlite()
        {
            using (var dbConn = new SQLiteConnection(string.Format("DataSource=\"{0}\"", _inputFilePath)))
            {
                dbConn.Open();
                var fileCount = GetFileCountFromSqlite(dbConn);

                using (IDbCommand cmd = dbConn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT Name, Data from FileDataTable " +
                        "JOIN FileNameTable " +
                          "ON FileDataTable.Id = FileNameTable.FileId " +
                        "WHERE Data NOT NULL AND Name NOT NULL " +
                        "ORDER BY FolderID, FileId";

                    using (var reader = cmd.ExecuteReader())
                    {
                        var count = 0;
                        while (reader.Read())
                        {
                            var name = reader.GetString(0);
                            var compressedData = (byte[])reader.GetValue(1);
                            var data = DecompressData(compressedData);
                            WriteToFile(name, data);
                            count += 1;
                            Console.WriteLine("{0} of {1} files done ({2}%).", count, fileCount, Math.Truncate(count * 100.0 / fileCount));
                        }
                    }
                }
            }
        }

        private static int GetFileCountFromSqlite(SQLiteConnection dbConn)
        {
            var fileCount = 0;
            using (IDbCommand cmd = dbConn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM FileNameTable " +
                    "WHERE Name NOT NULL ";

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        fileCount = reader.GetInt32(0);
                    }
                }
            }

            return fileCount;
        }

        private static void WriteToFile(string name, byte[] data)
        {
            var fileName = Path.GetFileName(name);
            var directory = Path.GetDirectoryName(name);

            if (string.IsNullOrEmpty(directory))
            {
                directory = _outputPath;
            }
            else
            {
                directory = Path.Combine(_outputPath, directory);
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var absolutePath = Path.Combine(directory, fileName);
            using (var writer = new FileStream(absolutePath, FileMode.Create))
            {
                writer.Write(data, 0, data.Length);
            }
        }

        private static byte[] AddGzipHeader(byte[] input)
        {
            using (var mem = new MemoryStream())
            {
                mem.Write(new byte[] { 0x1F, 0x8B }, 0, 2);    // GZip Magic Number
                mem.WriteByte(0x8);                          // Compression Mode: Deflate
                mem.WriteByte(0x0);                          // Flags: FTEXT
                mem.Write(input, 0, input.Length);           // add input
                return mem.ToArray();
            }
        }

        private static byte[] DecompressData(byte[] compressed)
        {
            var gzipData = AddGzipHeader(compressed);
            var decompressed = DecompressGzip(gzipData);
            return decompressed;
        }

        private static byte[] DecompressGzip(byte[] input)
        {
            using (var mem = new MemoryStream(input))
            {
                using (var result = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int read;

                    using (var decompressionStream = new GZipStream(mem, CompressionMode.Decompress))
                    {
                        do
                        {
                            read = decompressionStream.Read(buffer, 0, buffer.Length);
                            if (read > 0)
                            {
                                result.Write(buffer, 0, read);
                            }
                        }
                        while (read > 0);
                    }

                    return result.ToArray();
                }
            }
        }
    }
}