' GUI-only entry point: never use cscript.exe or a console launcher.
' Arguments: target .ps1, Base64 UTF-8 JSON string-array, report JSON path.
' A hidden bridge creates the actual target with native CreateNoWindow.
Option Explicit
Dim args, shell, files, bridge, executable, command
Set args = WScript.Arguments
If args.Count <> 3 Then WScript.Quit 64
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
bridge = files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "launch_hidden.ps1")
executable = shell.ExpandEnvironmentStrings("%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe")
command = Quote(executable) & " -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " & Quote(bridge) & " -ScriptPath " & Quote(args(0)) & " -ArgumentsBase64 " & Quote(args(1)) & " -ReportPath " & Quote(args(2))
shell.Run command, 0, False

Function Quote(value)
    Dim i, slashes, encoded, character
    encoded = """"
    slashes = 0
    For i = 1 To Len(value)
        character = Mid(value, i, 1)
        If character = "\" Then
            slashes = slashes + 1
        ElseIf character = """" Then
            encoded = encoded & String(slashes * 2 + 1, "\") & character
            slashes = 0
        Else
            encoded = encoded & String(slashes, "\") & character
            slashes = 0
        End If
    Next
    Quote = encoded & String(slashes * 2, "\") & """"
End Function
