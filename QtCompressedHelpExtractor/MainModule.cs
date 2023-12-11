﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace QtCompressedHelpExtractor
{
    static class MainModule
    {
        private static string _contentFilePath;
        private static string _coverFile;
        private static string _inputFilePath;
        private static string _outputPath;
        private static string _outputPdfPath;
        private static string _wkhtmltopdf;

        public static int Main()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            if (Environment.GetCommandLineArgs().Length < 3)
            {
                Console.WriteLine("{0} <sqlite_file_path> <output_directory>", Process.GetCurrentProcess().ProcessName);
                return 1;
            }

            _coverFile = "wincc_oa.htm";
            _inputFilePath = Environment.GetCommandLineArgs()[1];
            _outputPath = Environment.GetCommandLineArgs()[2];
            _outputPdfPath = "output.pdf";
            _contentFilePath = Path.Combine(_outputPath, "content.txt");

            Directory.SetCurrentDirectory(_outputPath);

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
                Console.WriteLine("Looking for wkhtmltopdf.");
                GetWkhtmltopdfPath();

                Console.WriteLine("Starting extraction ...");
                ExportFileOrder();
                ExtractFilesFromSqlite();

                Console.WriteLine("Creating the PDF: " + _outputPdfPath);
                CreatePdf();

                Console.WriteLine("Finished.");
                return 0;
            }
            catch (WkhtmltopdfNotFoundException)
            {
                Console.WriteLine("Wkhtmltopdf not found. Please install it first.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unknown error: {0}\n{1}", ex.Message, ex.StackTrace);
                return 1;
            }
        }

        private static void GetWkhtmltopdfPath()
        {
            string regPath = @"SOFTWARE\wkhtmltopdf";
            using var regKey = Registry.LocalMachine.OpenSubKey(regPath);

            if (regKey is null)
            {
                throw new WkhtmltopdfNotFoundException("wkhtmltopdf not found, abort.");
            }

            _wkhtmltopdf = (string)regKey.GetValue("PdfPath");
        }

        private static void CreatePdf()
        {
            var content = string.Empty;
            var output = string.Empty;
            var args = "--footer-center \"[page]/[topage]\" " +
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
                    content += string.Format(" {0}", _outputPdfPath);
                }
            }

            Process process = new Process { 
                StartInfo = { 
                    FileName = _wkhtmltopdf, 
                    Arguments = args, 
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                } 
            };
            Console.WriteLine("Starting wkhtmltopdf, please wait...");
            process.Start();
            process.StandardInput.WriteLine(content);
            output = process.StandardOutput.ReadToEnd();
            Console.WriteLine(output);
            process.WaitForExit();
        }

        private static void ExportFileOrder()
        {
            var data = GetTableOfContentAsBlob();
            var filePaths = ExtractFilePathsFromTableOfContent(data);
            var pathList = RemoveAnchorLinks(filePaths.Keys.ToList());
            var joinedPaths = string.Join(" ", pathList);

            using var writer = new StreamWriter(_contentFilePath);
            writer.WriteLine(joinedPaths);
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
                int titleLength;

                string filePath;
                string title;

                i += 2; // skip null-bytes
                // level = data[i + 1] + 256 * data[i]; // big endian
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

                using var cmd = dbConn.CreateCommand();
                cmd.CommandText =
                    "SELECT Data from ContentsTable " +
                    "WHERE ID = 1";

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    data = (byte[])reader.GetValue(0);
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

                using IDbCommand cmd = dbConn.CreateCommand();
                cmd.CommandText =
                    "SELECT Name, Data from FileDataTable " +
                    "JOIN FileNameTable " +
                      "ON FileDataTable.Id = FileNameTable.FileId " +
                    "WHERE Data NOT NULL AND Name NOT NULL " +
                    "ORDER BY FolderID, FileId";

                using var reader = cmd.ExecuteReader();
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

        private static int GetFileCountFromSqlite(SQLiteConnection dbConn)
        {
            var fileCount = 0;
            using (IDbCommand cmd = dbConn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM FileNameTable " +
                    "WHERE Name NOT NULL ";

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    fileCount = reader.GetInt32(0);
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

        private static byte[] DecompressData(byte[] input)
        {
            input = input.Skip(6).ToArray();

            using (var sOutput = new MemoryStream())
            using (var sCompressed = new MemoryStream())
            {
                sCompressed.Write(input, 0, input.Length);
                sCompressed.Position = 0;
                using (var decomp = new DeflateStream(sCompressed, CompressionMode.Decompress))
                {
                    decomp.CopyTo(sOutput);
                }
                return sOutput.ToArray();
            }
        }
    }

    [Serializable]
    class WkhtmltopdfNotFoundException : Exception
    {
        public WkhtmltopdfNotFoundException()
        {

        }

        public WkhtmltopdfNotFoundException(string message)
        : base(message)
        {
        }

        public WkhtmltopdfNotFoundException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}