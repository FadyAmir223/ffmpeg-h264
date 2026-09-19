param([string]$InputPath, [string]$OutputPath)

$ErrorActionPreference = 'Stop'

function Get-Folder($label, $path) {
    if (-not $path) {
        Write-Host "Drag and drop the $label folder here, then press ENTER:"
        $path = Read-Host
    }
    if ($path.Length -ge 2 -and $path.StartsWith('"') -and $path.EndsWith('"')) {
        $path = $path.Substring(1, $path.Length - 2)
    }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "$label folder does not exist: $path"
    }
    (Get-Item -LiteralPath $path).FullName
}

function Send-GuiProgress($eventName, $index, $total, $path, $percent) {
    $message = [pscustomobject]@{
        Event = $eventName
        Index = $index
        Total = $total
        Path = $path
        Percent = $percent
    } | ConvertTo-Json -Compress
    [Console]::Out.WriteLine("H264GUI:$message")
}

try {
    $ffmpeg = Join-Path $PSScriptRoot 'ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
        throw 'ffmpeg.exe was not found next to h264.ps1.'
    }
    $ffprobe = Join-Path $PSScriptRoot 'ffprobe.exe'
    if (-not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
        throw 'ffprobe.exe was not found next to h264.ps1.'
    }

    $inputRoot = Get-Folder 'INPUT' $InputPath
    $outputRoot = Get-Folder 'OUTPUT' $OutputPath
    $inputPrefix = $inputRoot.TrimEnd('\') + '\'
    $outputPrefix = $outputRoot.TrimEnd('\') + '\'
    if ([string]::Equals($inputPrefix, $outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Input and output folders must be different.'
    }

    $videoExtensions = @('.mp4', '.mkv', '.webm', '.avi', '.mov', '.flv', '.ts', '.m4v')
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($inputRoot)
    $videos = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
            if ($item.PSIsContainer) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                    ($item.FullName + '\').StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $pending.Push($item.FullName)
            } elseif ($videoExtensions -contains $item.Extension) {
                $videos.Add($item)
            }
        }
    }

    $converted = 0
    $skipped = 0
    $failed = 0
    $index = 0
    Send-GuiProgress 'Total' 0 $videos.Count '' 0

    foreach ($item in $videos) {
            $index++
            Send-GuiProgress 'File' $index $videos.Count $item.FullName 0
            $relativePath = $item.FullName.Substring($inputPrefix.Length)
            $relativeDirectory = [IO.Path]::GetDirectoryName($relativePath)
            $destinationDirectory = if ($relativeDirectory) { Join-Path $outputRoot $relativeDirectory } else { $outputRoot }
            $target = Join-Path $destinationDirectory ($item.BaseName + '.mp4')
            if (Test-Path -LiteralPath $target) {
                Write-Host "SKIP: $target"
                $skipped++
                Send-GuiProgress 'Skipped' $index $videos.Count $item.FullName 100
                continue
            }

            [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
            $temporary = Join-Path $destinationDirectory ('.' + [guid]::NewGuid().ToString('N') + '.mp4')
            Write-Host "Converting: $($item.FullName)"
            try {
                $durationText = & $ffprobe -v error -show_entries format=duration -of 'default=noprint_wrappers=1:nokey=1' $item.FullName
                $duration = 0.0
                if ($durationText) {
                    [double]::TryParse(@($durationText)[-1], [Globalization.NumberStyles]::Float,
                        [Globalization.CultureInfo]::InvariantCulture, [ref]$duration) | Out-Null
                }

                & $ffmpeg -nostdin -hide_banner -loglevel error -progress pipe:1 -nostats -i $item.FullName -vf 'scale=800:480:force_original_aspect_ratio=decrease,pad=800:480:(ow-iw)/2:(oh-ih)/2' -c:v libx264 -profile:v baseline -level:v 2.0 -pix_fmt yuv420p -r 30000/1001 -b:v 2200k -c:a aac -profile:a aac_low -b:a 128k -ar 44100 -ac 2 $temporary |
                    ForEach-Object {
                        if ($duration -gt 0 -and $_ -match '^out_time_us=(\d+)$') {
                            $percent = [int][Math]::Min(99, [Math]::Floor(100 * [double]$Matches[1] / ($duration * 1000000)))
                            Send-GuiProgress 'Progress' $index $videos.Count $item.FullName $percent
                        }
                    }
                if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited with code $LASTEXITCODE" }
                [IO.File]::Move($temporary, $target)
                Write-Host "DONE: $target"
                $converted++
                Send-GuiProgress 'Done' $index $videos.Count $item.FullName 100
            } catch {
                Write-Warning "FAILED: $($item.FullName): $_"
                $failed++
                Send-GuiProgress 'Failed' $index $videos.Count $item.FullName 0
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            }
    }

    Write-Host "Finished: $converted converted, $skipped skipped, $failed failed."
    if ($failed -gt 0) { exit 1 }
} catch {
    [Console]::Error.WriteLine($_)
    exit 1
}
