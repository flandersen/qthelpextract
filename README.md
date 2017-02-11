# qthelpextract
Tool to extract files from Qt Compressed Help Files (*.qch). 

Just compile this tool with Visual Studio 2015 and execute:

QtCompressedHelpExtractor.exe <source-file> <target-directory>

For example if you want to export the html pages of the WinCC OA 3.14 manual:

<source-file> = C:\Siemens\Automation\WinCC_OA\3.14\help\en_US.utf8\WinCC_OA.qch
<target-directory> = C:\Temp\WinCC-OA-Manual-3.14\

*.qch files are SQLite 3 database files.

What you end up is all HTML files and resource files including the folder hierarchy.
