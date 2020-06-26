Imports System.Data.SQLite
Imports System.IO
Imports System.IO.Compression
Imports System.Linq

Module MainModule

    Private Const BUFFER_SIZE As Integer = 4095

    Private _contentFilePath As String
    Private _inputFilePath As String
    Private _outputPath As String
    Private _wkhtmltopdfPath As String

    Function Main() As Integer

        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory)

        If Environment.GetCommandLineArgs.Length < 3 Then
            Console.WriteLine("{0} <sqlite_file_path> <output_directory>", Process.GetCurrentProcess.ProcessName)
            Return 1
        End If

        _inputFilePath = Environment.GetCommandLineArgs(1)
        _outputPath = Environment.GetCommandLineArgs(2)

        If String.IsNullOrEmpty(_inputFilePath) OrElse Not File.Exists(_inputFilePath) Then
            Console.WriteLine("Error: SQLite file does not exist.")
            Console.ReadKey()
            Return 2
        End If

        Try
            ' Check if target path is valid.
            With New DirectoryInfo(_outputPath)
            End With

        Catch ex As Exception
            Console.WriteLine("Error: Incorrect target folder.")
            Console.ReadKey()
            Return 2
        End Try

        Try
            Console.WriteLine("Starting extraction ...")
            _contentFilePath = Path.Combine(_outputPath, "content.txt")

            ExportFileOrder()
            ExtractFilesFromSqlite()

            Console.WriteLine("Finished.")
            Return 0

        Catch ex As Exception
            Console.WriteLine("Unknown error: {0}{1}{2}", ex.Message, vbCrLf, ex.StackTrace)
            Return 1
        End Try
    End Function

    Private Sub ExportFileOrder()
        Dim data() As Byte
        Dim filePaths As IDictionary(Of String, String)
        Dim joinedPaths As String
        Dim pathList As IList(Of String)

        data = GetTableOfContentAsBlob()
        filePaths = ExtractFilePathsFromTableOfContent(data)
        pathList = filePaths.Keys.ToList()
        pathList = RemoveAnchorLinks(pathList)
        joinedPaths = String.Join(" ", pathList)

        Using writer As New StreamWriter(_contentFilePath)
            writer.WriteLine(joinedPaths)
        End Using
    End Sub

    Private Function RemoveAnchorLinks(filePaths As IList(Of String)) As IList(Of String)
        Dim withoutAnchor As String
        Dim withoutAnchors As New List(Of String)
        Dim regex As New Text.RegularExpressions.Regex("#{1}\w+")

        For Each path As String In filePaths
            withoutAnchor = regex.Replace(path, String.Empty)
            withoutAnchors.Add(withoutAnchor)
        Next

        Return withoutAnchors
    End Function

    Private Function ExtractFilePathsFromTableOfContent(data() As Byte) As Dictionary(Of String, String)
        Dim filePaths As New Dictionary(Of String, String)
        Dim filePath As String
        Dim filePathLength As Integer
        Dim i As Integer
        Dim lastIndex As Integer
        Dim level As Integer
        Dim title As String
        Dim titleLength As Integer

        lastIndex = data.Length - 1
        i = 0
        While i <= lastIndex
            i += 2 ' skip null-byte
            level = data(i + 1) + 256 * data(i) ' big endian
            i += 2

            If data(i) = &HFF AndAlso data(i + 1) = &HFF Then
                ' chapter has no file...
                filePath = String.Empty
                i += 4 ' consume four bytes
            Else
                ' chapter has file...
                i += 2 ' skip null-byte

                filePathLength = data(i + 1) + 256 * data(i) ' big endian
                i += 2

                filePath = Text.Encoding.BigEndianUnicode.GetString(data, i, filePathLength)
                i += filePathLength
            End If

            i += 2 ' skip null-byte

            titleLength = data(i + 1) + 256 * data(i) ' big endian
            i += 2

            title = Text.Encoding.BigEndianUnicode.GetString(data, i, titleLength)
            i += titleLength

            filePaths(filePath) = title
        End While

        Return filePaths
    End Function

    Private Function GetTableOfContentAsBlob() As Byte()
        Dim data As Byte() = Nothing

        Using dbConn As New SQLite.SQLiteConnection(String.Format("DataSource=""{0}""", _inputFilePath))
            dbConn.Open()

            Dim filePaths As New Dictionary(Of String, String)
            Dim totalCount As Integer = 0

            Using command As IDbCommand = dbConn.CreateCommand()
                command.CommandText =
                    "SELECT Data from ContentsTable " &
                    "WHERE ID = 1"

                Using reader As IDataReader = command.ExecuteReader
                    If reader.Read Then
                        data = reader.GetValue(0)
                    End If
                End Using
            End Using
        End Using

        Return data
    End Function

    Private Sub ExtractFilesFromSqlite()
        Using dbConn As New SQLiteConnection(String.Format("DataSource=""{0}""", _inputFilePath))
            dbConn.Open()

            Dim fileCount As Integer
            fileCount = GetFileCountFromSqlite(dbConn)

            Using command As IDbCommand = dbConn.CreateCommand()
                command.CommandText =
                    "SELECT Name, Data from FileDataTable " &
                    "JOIN FileNameTable ON FileDataTable.Id = FileNameTable.FileId " &
                    "WHERE Data NOT NULL AND Name NOT NULL " &
                    "ORDER BY FolderID, FileId"

                Dim count As Integer = 0

                Using reader As IDataReader = command.ExecuteReader
                    While reader.Read
                        Dim compressedData() As Byte
                        Dim data As Byte()
                        Dim name As String

                        name = reader.GetString(0)
                        compressedData = reader.GetValue(1)
                        data = DecompressData(compressedData)

                        WriteToFile(name, data)

                        count += 1
                        Console.WriteLine("{0} of {1} files done ({2}%).", count, fileCount, Math.Truncate(count * 100.0 / fileCount))
                    End While
                End Using
            End Using
        End Using
    End Sub

    Private Function GetFileCountFromSqlite(dbConn As SQLiteConnection) As Integer
        Dim fileCount As Integer = 0

        Using command As IDbCommand = dbConn.CreateCommand()
            command.CommandText =
                "SELECT COUNT(*) FROM FileNameTable " &
                "WHERE Name NOT NULL "

            Using reader As IDataReader = command.ExecuteReader
                If reader.Read Then
                    fileCount = reader.GetInt32(0)
                End If
            End Using
        End Using

        Return fileCount
    End Function

    Private Sub WriteToFile(name As String, data() As Byte)
        Dim absolutePath As String
        Dim fileName As String = Path.GetFileName(name)
        Dim directory As String = Path.GetDirectoryName(name)

        If String.IsNullOrEmpty(directory) Then
            directory = _outputPath
        Else
            directory = Path.Combine(_outputPath, directory)
        End If

        If Not IO.Directory.Exists(directory) Then
            IO.Directory.CreateDirectory(directory)
        End If

        absolutePath = Path.Combine(directory, fileName)

        Using writer As New FileStream(absolutePath, FileMode.Create)
            writer.Write(data, 0, data.Length)
        End Using
    End Sub

    Private Function AddGzipHeader(input() As Byte) As Byte()

        Using mem As New MemoryStream()
            mem.Write(New Byte() {&H1F, &H8B}, 0, 2)    ' GZip Magic Number
            mem.WriteByte(&H8)                          ' Compression Mode: Deflate
            mem.WriteByte(&H0)                          ' Flags: FTEXT
            mem.Write(input, 0, input.Length)           ' add input

            Return mem.ToArray
        End Using
    End Function

    Private Function DecompressData(compressed() As Byte) As Byte()
        Dim gzipData As Byte()
        Dim decompressed() As Byte

        gzipData = AddGzipHeader(compressed)
        decompressed = DecompressGzip(gzipData)

        Return decompressed
    End Function

    Private Function DecompressGzip(input As Byte()) As Byte()
        Using source = New MemoryStream(input)
            Dim buffer(BUFFER_SIZE) As Byte
            Dim read As Integer

            Using result As New MemoryStream()
                Using decompressionStream = New GZipStream(source, CompressionMode.Decompress)
                    Do
                        read = decompressionStream.Read(buffer, 0, buffer.Length)

                        If read > 0 Then
                            result.Write(buffer, 0, read)
                        End If
                    Loop While read > 0
                End Using

                Return result.ToArray
            End Using
        End Using
    End Function

End Module
