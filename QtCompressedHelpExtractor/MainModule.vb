Imports System.IO
Imports System.IO.Compression

Module MainModule
    Private Const BUFFER_SIZE As Integer = 4095
    Private _inputFilePath As String
    Private _targetFolder As String

    Function Main() As Integer

        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory)

        If Environment.GetCommandLineArgs.Length < 3 Then
            Console.WriteLine("{0} <sqlite_file_path> <output_directory>", Process.GetCurrentProcess.ProcessName)
            Return 1
        End If

        _inputFilePath = Environment.GetCommandLineArgs(1)
        _targetFolder = Environment.GetCommandLineArgs(2)

        If String.IsNullOrEmpty(_inputFilePath) OrElse Not File.Exists(_inputFilePath) Then
            Console.WriteLine("Error: SQLite file does not exist.")
            Console.ReadKey()
            Return 2
        End If

        Try
            ' Check if target path is valid.
            With New DirectoryInfo(_targetFolder)
            End With

        Catch ex As Exception
            Console.WriteLine("Error: Incorrect target folder.")
            Console.ReadKey()
            Return 2
        End Try

        Try
            Console.WriteLine("Starting extraction ...")

            ExtractFilesFromSqlite()

            Console.WriteLine("Finished.")
            Return 0

        Catch ex As Exception
            Console.WriteLine("Unknown error: {0}{1}{2}", ex.Message, vbCrLf, ex.StackTrace)
            Return 1
        End Try
    End Function

    Private Sub ExtractFilesFromSqlite()
        Using dbConn As New SQLite.SQLiteConnection(String.Format("DataSource=""{0}""", _inputFilePath))
            dbConn.Open()

            Dim totalCount As Integer = 0

            Using command As IDbCommand = dbConn.CreateCommand()
                command.CommandText =
                    "SELECT COUNT(*) FROM FileNameTable " &
                    "WHERE Name NOT NULL "

                Using reader As IDataReader = command.ExecuteReader
                    If reader.Read Then
                        totalCount = reader.GetInt32(0)
                    End If
                End Using
            End Using

            Using command As IDbCommand = dbConn.CreateCommand()
                command.CommandText =
                    "SELECT Name, Data from FileDataTable " &
                    "JOIN FileNameTable ON FileDataTable.Id = FileNameTable.FileId " &
                    "WHERE Data NOT NULL AND Name NOT NULL " &
                    "ORDER BY FolderID, FileId"

                Dim count As Integer = 0

                Using reader As IDataReader = command.ExecuteReader
                    While reader.Read

                        Dim data() As Byte
                        Dim name As String
                        Dim uncompressedData As Byte()

                        count += 1

                        name = reader.GetString(0)
                        data = reader.GetValue(1)
                        uncompressedData = DecompressData(data)

                        WriteToFile(name, uncompressedData)

                        Console.WriteLine("{0} of {1} files done ({2}%).", count, totalCount, Math.Truncate(count * 100.0 / totalCount))
                    End While
                End Using
            End Using
        End Using
    End Sub

    Private Sub WriteToFile(name As String, uncompressedData() As Byte)

        Dim fileName As String = Path.GetFileName(name)
        Dim folder As String = Path.GetDirectoryName(name)

        If Not String.IsNullOrEmpty(folder) Then
            folder = Path.Combine(_targetFolder, folder)
        Else
            folder = _targetFolder
        End If

        If Not Directory.Exists(folder) Then
            Directory.CreateDirectory(folder)
        End If

        Dim absolutePath As String = Path.Combine(folder, fileName)

        Using writer As New FileStream(absolutePath, FileMode.Create)
            writer.Write(uncompressedData, 0, uncompressedData.Length)
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
        Dim gzipData As Byte() = AddGzipHeader(compressed)
        Dim decompressed() As Byte = DecompressGzip(gzipData)

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
