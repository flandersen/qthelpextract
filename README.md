# qthelpextract

This tool can be used to convert the Qt Compressed Help File `*.qch` from Siemens WinCC OA to PDF. 

## Prerequisites
The following software must be installed on the system:
| Dependency | Source | Tested Version |
| ---------- | -------| ---------------|
| wkhtmltopdf | [website](https://wkhtmltopdf.org)| 0.12.6|

## Compiling the Source Code
Compile the C# project with [Visual Studio](https://visualstudio.microsoft.com/vs/community/).

## Using the Tool
`QtCompressedHelpExtractor.exe [source-file] [target-directory]`

For example if you want to export the html pages of the WinCC OA 3.14 manual:

[source-file] = "C:\Siemens\Automation\WinCC_OA\3.14\help\en_US.utf8\WinCC_OA.qch"
[target-directory] = "C:\Temp\WinCC-OA-Manual-3.14\"

## How does the Tool work
`*.qch` files are SQLite 3 database files.

The tool extracts and decompresses the <em>html</em> and other files from the help file into the `[target-directory]`. 

Then, it extracts the table of content from the help file into `[target-directory]\content.txt`. This determines the order of the html files in the final PDF.

Last but not least, wkhtmltopdf is used to create the PDF `[target-directory]\output.pdf`.

The last step can take several minutes and can use several gigabytes of RAM.