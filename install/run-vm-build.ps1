# Self-contained VM build for FileKeep.
# Embeds the fixed installer source + WinPE script (base64) so only ONE file
# needs to be transferred - no URLs to go stale.
#
# On the VM, run:
#   iwr "<this-script-url>" -OutFile C:\ub\run.ps1
#   C:\ub\run.ps1

$ErrorActionPreference = "Stop"

# Code signing configuration (all optional - build works without signing).
# Set these as environment variables on the build VM:
#   UB_SIGN_THUMBPRINT - Certificate thumbprint in LocalMachine\My store
#   UB_SIGN_PFX        - Path to .pfx/.p12 certificate file
#   UB_SIGN_PFX_PASSWORD - Password for the PFX file (consider using a secure vault)
#   UB_SKIP_SIGNING    - Set to "1" to explicitly skip signing
#
# The signing step uses signtool.exe from the Windows SDK. Timestamping is
# always applied so signatures remain valid after the cert expires.

function Invoke-CodeSigning {
    param([string[]]$Files)

    if ($env:UB_SKIP_SIGNING -eq "1") {
        Write-Host "  Signing skipped (UB_SKIP_SIGNING=1)."
        return
    }

    $signtool = $null
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe"
    )) {
        if (Test-Path $p) { $signtool = $p; break }
    }
    if (-not $signtool) {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    }
    if (-not $signtool) {
        Write-Host "  WARNING: signtool.exe not found - skipping code signing."
        Write-Host "  Install the Windows SDK to enable signing."
        return
    }

    $timestampUrl = "http://timestamp.digicert.com"
    $signed = 0

    foreach ($f in $Files) {
        if (-not (Test-Path $f)) {
            Write-Host "  WARNING: file not found, skipping: $f"
            continue
        }

        $args = @("sign", "/tr", $timestampUrl, "/td", "sha256", "/fd", "sha256")

        if ($env:UB_SIGN_THUMBPRINT) {
            $args += @("/sha1", $env:UB_SIGN_THUMBPRINT)
            Write-Host "  Signing $f with cert thumbprint $($env:UB_SIGN_THUMBPRINT.Substring(0,8))..."
        } elseif ($env:UB_SIGN_PFX) {
            $args += @("/f", $env:UB_SIGN_PFX)
            if ($env:UB_SIGN_PFX_PASSWORD) {
                $args += @("/p", $env:UB_SIGN_PFX_PASSWORD)
            }
            Write-Host "  Signing $f with PFX $env:UB_SIGN_PFX..."
        } else {
            Write-Host "  No signing certificate configured (set UB_SIGN_THUMBPRINT or UB_SIGN_PFX)."
            Write-Host "  Skipping code signing - build will be unsigned."
            return
        }

        $args += $f
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $f" }

        # Verify the signature
        & $signtool verify /pa $f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $f" }

        Write-Host "  Signed and verified: $f"
        $signed++
    }

    Write-Host "  Signed $signed file(s)."
}

# Log everything to C:\ub\build.log: RDP drops do not kill the script on the
# VM, so if the session disconnects, reconnect and read the log for progress.
$transcript = "C:\ub\build.log"
try { Stop-Transcript | Out-Null } catch { }
Start-Transcript -Path $transcript -Append | Out-Null
Write-Host "Logging to $transcript"

try {

Write-Host "=== 1/6 Writing embedded installer source + WinPE script ==="
$wxsB64 = @"
PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTgiPz4KPCEtLSBXaVggdjUgaW5zdGFsbGVyIGZvciBGaWxlS2VlcC4KICAgICBUaGUgYXBwcyBwdWJsaXNoIGFzIHNpbmdsZS1maWxlIGV4ZWN1dGFibGVzIChQdWJsaXNoU2luZ2xlRmlsZSBpbiB0aGUKICAgICBjc3Byb2opLCBzbyB0aGUgcGF5bG9hZCBpcyBhIGhhbmRmdWwgb2YgZXhwbGljaXQgZmlsZXMg4oCUIG5vIGhhcnZlc3RpbmcuCiAgICAgQnVpbGQgb24gV2luZG93cyB3aXRoIHRoZSBXaVggdjUgZG90bmV0IHRvb2w6CiAgICAgICB3aXggYnVpbGQgLWFyY2ggeDY0IC1kIFNyY1Jvb3Q9QzpcdWJcc3JjIC1kIENsaUJpbj1DOlx1YlxwdWJsaXNoXGNsaSAtZCBTZXJ2aWNlQmluPUM6XHViXHB1Ymxpc2hcc2VydmljZSAtZCBSZWNvdmVyeUJpbj1DOlx1YlxwdWJsaXNoXHJlY292ZXJ5IC1vIHVzZW5ldC1iYWNrdXAtMC44LjEteDY0Lm1zaSBpbnN0YWxsL1VzZW5ldEJhY2t1cC53eHMKICAgICBJTVBPUlRBTlQ6IHJlLXB1Ymxpc2ggd2l0aCB0aGUgY3VycmVudCBjc3Byb2ogKHNpbmdsZS1maWxlKSBiZWZvcmUgYnVpbGRpbmc7CiAgICAgdGhlIE1TSSBpbnN0YWxscyBvbmx5IHRoZSBmaWxlcyBsaXN0ZWQgYmVsb3cuCi0tPgo8V2l4IHhtbG5zPSJodHRwOi8vd2l4dG9vbHNldC5vcmcvc2NoZW1hcy92NC93eHMiPgogIDxQYWNrYWdlIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgIFZlcnNpb249IjAuOC4xIgogICAgICAgICAgIE1hbnVmYWN0dXJlcj0iSi4gVHJlaWNoZWwiCiAgICAgICAgICAgVXBncmFkZUNvZGU9IkY2RTBENkQxLTYxQTEtNDg0My04N0I5LTI4MjVBNDdBMThERCIKICAgICAgICAgICBTY29wZT0icGVyTWFjaGluZSI+CgogICAgPE1ham9yVXBncmFkZSBEb3duZ3JhZGVFcnJvck1lc3NhZ2U9IkEgbmV3ZXIgdmVyc2lvbiBvZiBGaWxlS2VlcCBpcyBhbHJlYWR5IGluc3RhbGxlZC4iIC8+CgogICAgPE1lZGlhIElkPSIxIiBDYWJpbmV0PSJwcm9kdWN0LmNhYiIgRW1iZWRDYWI9InllcyIgLz4KCiAgICA8U3RhbmRhcmREaXJlY3RvcnkgSWQ9IlByb2dyYW1GaWxlczY0MzJGb2xkZXIiPgogICAgICA8RGlyZWN0b3J5IElkPSJJTlNUQUxMRk9MREVSIiBOYW1lPSJGaWxlS2VlcCI+CiAgICAgICAgPERpcmVjdG9yeSBJZD0iQ0xJRElSIiBOYW1lPSJjbGkiIC8+CiAgICAgICAgPERpcmVjdG9yeSBJZD0iU0VSVklDRURJUiIgTmFtZT0ic2VydmljZSIgLz4KICAgICAgICA8RGlyZWN0b3J5IElkPSJXSVpBUkRESVIiIE5hbWU9IndpemFyZCIgLz4KICAgICAgICA8RGlyZWN0b3J5IElkPSJET0NTRElSIiBOYW1lPSJkb2NzIiAvPgogICAgICAgIDxEaXJlY3RvcnkgSWQ9IldJTlBFRElSIiBOYW1lPSJ3aW5wZSIgLz4KICAgICAgPC9EaXJlY3Rvcnk+CiAgICA8L1N0YW5kYXJkRGlyZWN0b3J5PgoKICAgIDwhLS0gQ0xJIChzaW5nbGUtZmlsZSBleGU7IG5hdGl2ZSBTUUxpdGUgaXMgYnVuZGxlZCBpbnNpZGUpIC0tPgogICAgPENvbXBvbmVudEdyb3VwIElkPSJDbGlGaWxlcyIgRGlyZWN0b3J5PSJDTElESVIiPgogICAgICA8Q29tcG9uZW50IElkPSJDbGlFeGUiIEd1aWQ9IioiPgogICAgICAgIDxGaWxlIElkPSJDbGlFeGVGaWxlIiBTb3VyY2U9IiQodmFyLkNsaUJpbilcRmlsZUtlZXAuZXhlIiBLZXlQYXRoPSJ5ZXMiIC8+CiAgICAgICAgPEZpbGUgSWQ9IlZzc0hlbHBlckV4ZSIgU291cmNlPSIkKHZhci5Wc3NCaW4pXEZpbGVLZWVwVnNzLmV4ZSIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlQ2xpUGRiIiBEaXJlY3Rvcnk9IkNMSURJUiIgTmFtZT0iKi5wZGIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgICAgPFJlbW92ZUZvbGRlciBJZD0iUmVtb3ZlQ2xpRGlyIiBEaXJlY3Rvcnk9IkNMSURJUiIgT249InVuaW5zdGFsbCIgLz4KICAgICAgPC9Db21wb25lbnQ+CiAgICA8L0NvbXBvbmVudEdyb3VwPgoKICAgIDxDb21wb25lbnRHcm91cCBJZD0iU2VydmljZVJlZ2lzdHJhdGlvbiIgRGlyZWN0b3J5PSJTRVJWSUNFRElSIj4KICAgICAgPENvbXBvbmVudCBJZD0iU2VydmljZUV4ZWN1dGFibGUiPgogICAgICAgIDxGaWxlIElkPSJTZXJ2aWNlRXhlIiBTb3VyY2U9IiQodmFyLlNlcnZpY2VCaW4pXEZpbGVLZWVwU2VydmljZS5leGUiIEtleVBhdGg9InllcyIgLz4KICAgICAgICA8U2VydmljZUluc3RhbGwgSWQ9IkJhY2t1cFNlcnZpY2UiCiAgICAgICAgICAgICAgICAgICAgICAgIFR5cGU9Im93blByb2Nlc3MiCiAgICAgICAgICAgICAgICAgICAgICAgIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgICAgICAgICAgICAgICBEaXNwbGF5TmFtZT0iRmlsZUtlZXAgU2VydmljZSIKICAgICAgICAgICAgICAgICAgICAgICAgRGVzY3JpcHRpb249IlNjaGVkdWxlZCBlbmNyeXB0ZWQgYmFja3VwcyB0byBVc2VuZXQuIgogICAgICAgICAgICAgICAgICAgICAgICBTdGFydD0iYXV0byIKICAgICAgICAgICAgICAgICAgICAgICAgQWNjb3VudD0iTG9jYWxTeXN0ZW0iCiAgICAgICAgICAgICAgICAgICAgICAgIEVycm9yQ29udHJvbD0ibm9ybWFsIiAvPgogICAgICAgIDxTZXJ2aWNlQ29udHJvbCBJZD0iQmFja3VwU2VydmljZUNvbnRyb2wiCiAgICAgICAgICAgICAgICAgICAgICAgIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgICAgICAgICAgICAgICBTdG9wPSJib3RoIgogICAgICAgICAgICAgICAgICAgICAgICBSZW1vdmU9InVuaW5zdGFsbCIKICAgICAgICAgICAgICAgICAgICAgICAgV2FpdD0idHJ1ZSIgLz4KICAgICAgICA8IS0tIENsZWFuIHVwIHJ1bnRpbWUtY3JlYXRlZCBmaWxlcyAoc2VydmljZSB3cml0ZXMgY29uZmlnL2xvZ3MgaGVyZSwgc2luZ2xlLWZpbGUgZXhlIGV4dHJhY3RzIG5hdGl2ZSBsaWJzKSAtLT4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlU2VydmljZUpzb24iIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0ic2VydmljZS5qc29uIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVTZXJ2aWNlTG9nIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE5hbWU9InNlcnZpY2UubG9nIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVTcWxpdGVEbGwiIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0iZV9zcWxpdGUzLmRsbCIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlU2VydmljZVBkYiIgRGlyZWN0b3J5PSJTRVJWSUNFRElSIiBOYW1lPSIqLnBkYiIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlQXNwTmV0RGxsIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE5hbWU9ImFzcG5ldGNvcmV2Ml9pbnByb2Nlc3MuZGxsIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVXZWJDb25maWciIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0id2ViLmNvbmZpZyIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVTZXJ2aWNlRGlyIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgIDwvQ29tcG9uZW50PgogICAgPC9Db21wb25lbnRHcm91cD4KCiAgICA8IS0tIFJlY292ZXJ5IHdpemFyZCAoc2luZ2xlLWZpbGUgZXhlOyBuYXRpdmUgU1FMaXRlIGlzIGJ1bmRsZWQgaW5zaWRlKSAtLT4KICAgIDxDb21wb25lbnRHcm91cCBJZD0iUmVjb3ZlcnlGaWxlcyIgRGlyZWN0b3J5PSJXSVpBUkRESVIiPgogICAgICA8Q29tcG9uZW50IElkPSJSZWNvdmVyeUV4ZSIgR3VpZD0iKiI+CiAgICAgICAgPEZpbGUgSWQ9IlJlY292ZXJ5RXhlRmlsZSIgU291cmNlPSIkKHZhci5SZWNvdmVyeUJpbilcRmlsZUtlZXBSZWNvdmVyeS5leGUiIEtleVBhdGg9InllcyIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVXaXphcmREaXIiIERpcmVjdG9yeT0iV0laQVJERElSIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICA8L0NvbXBvbmVudD4KICAgIDwvQ29tcG9uZW50R3JvdXA+CgogICAgPCEtLSBTZXJ2aWNlIGNvbmZpZyBleGFtcGxlICsgcmVjb3ZlcnkgcnVuYm9vayAtLT4KICAgIDxDb21wb25lbnRHcm91cCBJZD0iRG9jcyIgRGlyZWN0b3J5PSJET0NTRElSIj4KICAgICAgPENvbXBvbmVudCBJZD0iRG9jc0NvbXBvbmVudCIgR3VpZD0iNDM2MzFGMDAtMjZDQi00NkE3LUJEOEYtMzhEQjQ0RUM0QTU0Ij4KICAgICAgICA8RmlsZSBTb3VyY2U9IiQodmFyLlNyY1Jvb3QpXHNyY1xVc2VuZXRCYWNrdXAuU2VydmljZVxzZXJ2aWNlLmV4YW1wbGUuanNvbiIgTmFtZT0ic2VydmljZS5leGFtcGxlLmpzb24iIC8+CiAgICAgICAgPEZpbGUgU291cmNlPSIkKHZhci5TcmNSb290KVxkb2NzXFJFQ09WRVJZLm1kIiBOYW1lPSJSRUNPVkVSWS5tZCIgLz4KICAgICAgICA8RmlsZSBTb3VyY2U9IiQodmFyLlNyY1Jvb3QpXExJQ0VOU0UiIE5hbWU9IkxJQ0VOU0UudHh0IiAvPgogICAgICAgIDxGaWxlIFNvdXJjZT0iJCh2YXIuU3JjUm9vdClcZG9jc1xUSElSRC1QQVJUWS1OT1RJQ0VTLm1kIiBOYW1lPSJUSElSRC1QQVJUWS1OT1RJQ0VTLm1kIiAvPgogICAgICAgIDxSZW1vdmVGb2xkZXIgSWQ9IlJlbW92ZURvY3NEaXIiIERpcmVjdG9yeT0iRE9DU0RJUiIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVJbnN0YWxsRGlyIiBEaXJlY3Rvcnk9IklOU1RBTExGT0xERVIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgIDwvQ29tcG9uZW50PgogICAgPC9Db21wb25lbnRHcm91cD4KCiAgICA8IS0tIFdpblBFIGJ1aWxkIHNjcmlwdCAodXNlZCBieSB0aGUgZGFzaGJvYXJkJ3MgV2luUEUgSVNPIEdVSSkgLS0+CiAgICA8Q29tcG9uZW50R3JvdXAgSWQ9IldpblBFRmlsZXMiIERpcmVjdG9yeT0iV0lOUEVESVIiPgogICAgICA8Q29tcG9uZW50IElkPSJXaW5QRUJ1aWxkU2NyaXB0IiBHdWlkPSIqIj4KICAgICAgICA8RmlsZSBTb3VyY2U9IiQodmFyLlNyY1Jvb3QpXHdpbnBlXGJ1aWxkLXdpbnBlLnBzMSIgTmFtZT0iYnVpbGQtd2lucGUucHMxIiBLZXlQYXRoPSJ5ZXMiIC8+CiAgICAgICAgPFJlbW92ZUZvbGRlciBJZD0iUmVtb3ZlV2luUEVEaXIiIERpcmVjdG9yeT0iV0lOUEVESVIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgIDwvQ29tcG9uZW50PgogICAgPC9Db21wb25lbnRHcm91cD4KCiAgICA8RmVhdHVyZSBJZD0iUHJvZHVjdEZlYXR1cmUiIFRpdGxlPSJGaWxlS2VlcCIgTGV2ZWw9IjEiPgogICAgICA8Q29tcG9uZW50R3JvdXBSZWYgSWQ9IkNsaUZpbGVzIiAvPgogICAgICA8Q29tcG9uZW50R3JvdXBSZWYgSWQ9IlNlcnZpY2VSZWdpc3RyYXRpb24iIC8+CiAgICAgIDxDb21wb25lbnRHcm91cFJlZiBJZD0iUmVjb3ZlcnlGaWxlcyIgLz4KICAgICAgPENvbXBvbmVudEdyb3VwUmVmIElkPSJEb2NzIiAvPgogICAgICA8Q29tcG9uZW50R3JvdXBSZWYgSWQ9IldpblBFRmlsZXMiIC8+CiAgICA8L0ZlYXR1cmU+CgogICAgPCEtLSBBZGQvUmVtb3ZlIFByb2dyYW1zIGljb24gLS0+CiAgICA8SWNvbiBJZD0iRmlsZUtlZXBJY29uIiBTb3VyY2VGaWxlPSIkKHZhci5TcmNSb290KS9hc3NldHMvZmlsZWtlZXAuaWNvIiAvPgogICAgPFByb3BlcnR5IElkPSJBUlBQUk9EVUNUSUNPTiIgVmFsdWU9IkZpbGVLZWVwSWNvbiIgLz4KCiAgICA8IS0tIEluc3RhbGxlciBiYW5uZXIgKHRvcCkgYW5kIGRpYWxvZyAoc2lkZSkgYml0bWFwcyAtLT4KICAgIDxXaXhWYXJpYWJsZSBJZD0iV2l4VUlCYW5uZXJCbXAiIFZhbHVlPSIkKHZhci5TcmNSb290KS9hc3NldHMvZmlsZWtlZXAtYmFubmVyLmJtcCIgLz4KICAgIDxXaXhWYXJpYWJsZSBJZD0iV2l4VUlEaWFsb2dCbXAiIFZhbHVlPSIkKHZhci5TcmNSb290KS9hc3NldHMvZmlsZWtlZXAtZGlhbG9nLmJtcCIgLz4KICA8L1BhY2thZ2U+CjwvV2l4Pgo=
"@
$winpeB64 = @"
IyBCdWlsZHMgYSBXaW5QRSBJU08gd2l0aCB0aGUgRmlsZUtlZXAgcmVjb3ZlcnkgdG9vbHMuCiMgUnVuIG9uIFdpbmRvd3Mgd2l0aCB0aGUgQURLICsgV2luUEUgYWRkLW9uIGluc3RhbGxlZCwgYXMgQWRtaW5pc3RyYXRvci4KIwojIFR3byBtb2RlczoKIyAgIDEuIFNvdXJjZSBtb2RlIChkZXYpOiBwdWJsaXNoZXMgZnJvbSBhIHNvdXJjZSB0cmVlLgojICAgICAgLlx3aW5wZVxidWlsZC13aW5wZS5wczEgLVNvdXJjZURpciBDOlx1YlxzcmMKIyAgIDIuIEJpbmFyeSBtb2RlIChpbnN0YWxsZWQpOiB1c2VzIHByZS1wdWJsaXNoZWQgYmluYXJpZXMsIGUuZy4gZnJvbQojICAgICAgQzpcUHJvZ3JhbSBGaWxlc1xGaWxlS2VlcC4gVGhlIGRhc2hib2FyZCdzIFdpblBFIEdVSSB1c2VzIHRoaXMgbW9kZS4KIyAgICAgIC5cd2lucGVcYnVpbGQtd2lucGUucHMxIC1CaW5hcnlEaXIgIkM6XFByb2dyYW0gRmlsZXNcRmlsZUtlZXAiCiMKIyBJbiBiaW5hcnkgbW9kZSB0aGUgbGF5b3V0IGlzIGV4cGVjdGVkIHRvIGJlOgojICAgPEJpbmFyeURpcj5cY2xpXEZpbGVLZWVwLmV4ZQojICAgPEJpbmFyeURpcj5cd2l6YXJkXEZpbGVLZWVwUmVjb3ZlcnkuZXhlCiMgICA8QmluYXJ5RGlyPlxkb2NzXFJFQ09WRVJZLm1kCiMKIyBPdXRwdXQ6IEM6XHdpbnBlXGZpbGVrZWVwLXdpbnBlLmlzbwoKcGFyYW0oCiAgICBbc3RyaW5nXSRTb3VyY2VEaXIgPSAiQzpcdWJcc3JjIiwKICAgIFtzdHJpbmddJEJpbmFyeURpciA9ICIiLAogICAgW3N0cmluZ10kV29ya0RpciA9ICJDOlx3aW5wZSIsCiAgICBbc3RyaW5nXSRTdGFnZURpciA9ICJDOlx3aW5wZS1zdGFnZSIsCiAgICBbc3RyaW5nXSRJc29QYXRoID0gIkM6XHdpbnBlXGZpbGVrZWVwLXdpbnBlLmlzbyIKKQoKJEVycm9yQWN0aW9uUHJlZmVyZW5jZSA9ICJTdG9wIgoKZnVuY3Rpb24gRmFpbCgkbXNnKSB7IFdyaXRlLUVycm9yICRtc2c7IGV4aXQgMSB9CgojIC0tLSBQcmVjb25kaXRpb25zIC0tLQppZiAoLW5vdCAoW1NlY3VyaXR5LlByaW5jaXBhbC5XaW5kb3dzUHJpbmNpcGFsXVtTZWN1cml0eS5QcmluY2lwYWwuV2luZG93c0lkZW50aXR5XTo6R2V0Q3VycmVudCgpKS5Jc0luUm9sZShbU2VjdXJpdHkuUHJpbmNpcGFsLldpbmRvd3NCdWlsdEluUm9sZV06OkFkbWluaXN0cmF0b3IpKSB7CiAgICBGYWlsICJSdW4gYXMgQWRtaW5pc3RyYXRvci4iCn0KIyBUaGUgQURLIGluc3RhbGxlciBkb2VzIG5vdCBwdXQgRGVwbG95bWVudCBUb29scyBvbiBQQVRILCBhbmQgdGhlIHRvb2xzIGFyZQojIG5vdCBhbHdheXMgYXQgdGhlIHRleHRib29rIGxvY2F0aW9uLCBzbyBwcm9iZSB0aGUgc3RhbmRhcmQga2l0IGRpcnMsIHRoZW4KIyBQQVRILCB0aGVuIHNlYXJjaCB0aGUga2l0IHRyZWUgKHNtYWxsKSBiZWZvcmUgZ2l2aW5nIHVwLgpmdW5jdGlvbiBGaW5kLUFka1Rvb2woW3N0cmluZ10kbmFtZSkgewogICAgJGNtZCA9IEdldC1Db21tYW5kICRuYW1lIC1FcnJvckFjdGlvbiBTaWxlbnRseUNvbnRpbnVlCiAgICBpZiAoJGNtZCkgeyByZXR1cm4gJGNtZC5Tb3VyY2UgfQogICAgJGtpdFJvb3RzID0gQCgKICAgICAgICAiQzpcUHJvZ3JhbSBGaWxlcyAoeDg2KVxXaW5kb3dzIEtpdHNcMTBcQXNzZXNzbWVudCBhbmQgRGVwbG95bWVudCBLaXQiLAogICAgICAgICJDOlxQcm9ncmFtIEZpbGVzXFdpbmRvd3MgS2l0c1wxMFxBc3Nlc3NtZW50IGFuZCBEZXBsb3ltZW50IEtpdCIKICAgICkKICAgIGZvcmVhY2ggKCRyb290IGluICRraXRSb290cykgewogICAgICAgICRwcm9iZSA9IEpvaW4tUGF0aCAkcm9vdCAiRGVwbG95bWVudCBUb29sc1wkbmFtZSIKICAgICAgICBpZiAoVGVzdC1QYXRoICRwcm9iZSkgeyByZXR1cm4gJHByb2JlIH0KICAgIH0KICAgIGZvcmVhY2ggKCRyb290IGluICRraXRSb290cykgewogICAgICAgIGlmIChUZXN0LVBhdGggJHJvb3QpIHsKICAgICAgICAgICAgJGZvdW5kID0gR2V0LUNoaWxkSXRlbSAkcm9vdCAtRmlsdGVyICRuYW1lIC1SZWN1cnNlIC1FcnJvckFjdGlvbiBTaWxlbnRseUNvbnRpbnVlIHwKICAgICAgICAgICAgICAgICAgICAgU2VsZWN0LU9iamVjdCAtRmlyc3QgMSAtRXhwYW5kUHJvcGVydHkgRnVsbE5hbWUKICAgICAgICAgICAgaWYgKCRmb3VuZCkgeyByZXR1cm4gJGZvdW5kIH0KICAgICAgICB9CiAgICB9CiAgICByZXR1cm4gJG51bGwKfQpmb3JlYWNoICgkdG9vbCBpbiBAKCJjb3B5cGUuY21kIiwgIk1ha2VXaW5QRU1lZGlhLmNtZCIpKSB7CiAgICAkdG9vbFBhdGggPSBGaW5kLUFka1Rvb2wgJHRvb2wKICAgIGlmICgtbm90ICR0b29sUGF0aCkgeyBGYWlsICIkdG9vbCBub3QgZm91bmQuIEluc3RhbGwgQURLIERlcGxveW1lbnQgVG9vbHMgKyBXaW5QRSBhZGQtb24uIiB9CiAgICAkdG9vbERpciA9IFNwbGl0LVBhdGggJHRvb2xQYXRoCiAgICBpZiAoJGVudjpQQVRIIC1ub3RsaWtlICIqJHRvb2xEaXIqIikgeyAkZW52OlBBVEggPSAiJHRvb2xEaXI7JGVudjpQQVRIIiB9CiAgICBXcml0ZS1Ib3N0ICIkdG9vbCAtPiAkdG9vbFBhdGgiCn0KaWYgKC1ub3QgKEdldC1Db21tYW5kIGRpc20uZXhlIC1FcnJvckFjdGlvbiBTaWxlbnRseUNvbnRpbnVlKSkgeyBGYWlsICJkaXNtLmV4ZSBub3QgZm91bmQuIiB9CgojIC0tLSBQdWJsaXNoIHNlbGYtY29udGFpbmVkIGJpbmFyaWVzIC0tLQojIE5PVEU6IHB1Ymxpc2ggaW50byBTdGFnZURpciwgTk9UIFdvcmtEaXIg4oCUIFdvcmtEaXIgaXMgd2lwZWQgYmVsb3cgYnkgY29weXBlLgokY2xpT3V0ID0gSm9pbi1QYXRoICRTdGFnZURpciAiY2xpIgokd2l6T3V0ID0gSm9pbi1QYXRoICRTdGFnZURpciAicmVjb3ZlcnkiCmlmICgkQmluYXJ5RGlyIC1uZSAiIikgewogICAgIyBCaW5hcnkgbW9kZTogY29weSBwcmUtcHVibGlzaGVkIGJpbmFyaWVzIGZyb20gdGhlIGluc3RhbGxlZCBsYXlvdXQuCiAgICAjIE5vIHNvdXJjZSB0cmVlIG9yIGRvdG5ldCBTREsgcmVxdWlyZWQuCiAgICBXcml0ZS1Ib3N0ICJCaW5hcnkgbW9kZTogY29weWluZyBwcmUtcHVibGlzaGVkIGJpbmFyaWVzIGZyb20gJEJpbmFyeURpci4uLiIKICAgICRzcmNDbGkgPSBKb2luLVBhdGggJEJpbmFyeURpciAiY2xpXEZpbGVLZWVwLmV4ZSIKICAgICRzcmNXaXogPSBKb2luLVBhdGggJEJpbmFyeURpciAid2l6YXJkXEZpbGVLZWVwUmVjb3ZlcnkuZXhlIgogICAgaWYgKC1ub3QgKFRlc3QtUGF0aCAkc3JjQ2xpKSkgeyBGYWlsICJDTEkgbm90IGZvdW5kOiAkc3JjQ2xpIiB9CiAgICBpZiAoLW5vdCAoVGVzdC1QYXRoICRzcmNXaXopKSB7IEZhaWwgIldpemFyZCBub3QgZm91bmQ6ICRzcmNXaXoiIH0KICAgIE5ldy1JdGVtIC1JdGVtVHlwZSBEaXJlY3RvcnkgLUZvcmNlIC1QYXRoICRjbGlPdXQgfCBPdXQtTnVsbAogICAgTmV3LUl0ZW0gLUl0ZW1UeXBlIERpcmVjdG9yeSAtRm9yY2UgLVBhdGggJHdpek91dCB8IE91dC1OdWxsCiAgICBDb3B5LUl0ZW0gKEpvaW4tUGF0aCAkQmluYXJ5RGlyICJjbGlcKiIpICRjbGlPdXQgLVJlY3Vyc2UgLUZvcmNlCiAgICBDb3B5LUl0ZW0gKEpvaW4tUGF0aCAkQmluYXJ5RGlyICJ3aXphcmRcKiIpICR3aXpPdXQgLVJlY3Vyc2UgLUZvcmNlCn0KZWxzZSB7CiAgICBXcml0ZS1Ib3N0ICJQdWJsaXNoaW5nIENMSSBhbmQgcmVjb3Zlcnkgd2l6YXJkIChzZWxmLWNvbnRhaW5lZCB3aW4teDY0KS4uLiIKICAgIGRvdG5ldCBwdWJsaXNoIChKb2luLVBhdGggJFNvdXJjZURpciAic3JjXFVzZW5ldEJhY2t1cC5DbGlcVXNlbmV0QmFja3VwLkNsaS5jc3Byb2oiKSBgCiAgICAgICAgLWMgUmVsZWFzZSAtciB3aW4teDY0IC0tc2VsZi1jb250YWluZWQgLW8gJGNsaU91dAogICAgaWYgKCRMQVNURVhJVENPREUgLW5lIDApIHsgRmFpbCAiQ0xJIHB1Ymxpc2ggZmFpbGVkLiIgfQogICAgZG90bmV0IHB1Ymxpc2ggKEpvaW4tUGF0aCAkU291cmNlRGlyICJzcmNcVXNlbmV0QmFja3VwLlJlY292ZXJ5XFVzZW5ldEJhY2t1cC5SZWNvdmVyeS5jc3Byb2oiKSBgCiAgICAgICAgLWMgUmVsZWFzZSAtciB3aW4teDY0IC0tc2VsZi1jb250YWluZWQgLW8gJHdpek91dAogICAgaWYgKCRMQVNURVhJVENPREUgLW5lIDApIHsgRmFpbCAiV2l6YXJkIHB1Ymxpc2ggZmFpbGVkLiIgfQp9CgojIC0tLSBCdWlsZCBXaW5QRSBiYXNlIC0tLQppZiAoVGVzdC1QYXRoICRXb3JrRGlyKSB7IFJlbW92ZS1JdGVtICRXb3JrRGlyIC1SZWN1cnNlIC1Gb3JjZSB9CldyaXRlLUhvc3QgIlJ1bm5pbmcgY29weXBlLi4uIgpjb3B5cGUuY21kIGFtZDY0ICRXb3JrRGlyCmlmICgkTEFTVEVYSVRDT0RFIC1uZSAwKSB7IEZhaWwgImNvcHlwZSBmYWlsZWQuIiB9CgokbW91bnREaXIgPSBKb2luLVBhdGggJFdvcmtEaXIgIm1vdW50IgokYm9vdFdpbSA9IEpvaW4tUGF0aCAkV29ya0RpciAibWVkaWFcc291cmNlc1xib290LndpbSIKCldyaXRlLUhvc3QgIk1vdW50aW5nIGJvb3Qud2ltLi4uIgpkaXNtLmV4ZSAvTW91bnQtSW1hZ2UgL0ltYWdlRmlsZTokYm9vdFdpbSAvaW5kZXg6MSAvTW91bnREaXI6JG1vdW50RGlyCmlmICgkTEFTVEVYSVRDT0RFIC1uZSAwKSB7IEZhaWwgIkRJU00gbW91bnQgZmFpbGVkLiIgfQoKdHJ5IHsKICAgICMgLS0tIFdpblBFIG9wdGlvbmFsIGNvbXBvbmVudHMgLS0tCiAgICAjIFRoZSB3aXphcmQncyBkcml2ZSBwaWNrZXIgdXNlcyBXTUkgKFN5c3RlbS5NYW5hZ2VtZW50KTsgdGhlIGJhc2UgV2luUEUKICAgICMgaW1hZ2UgbWF5IG5vdCBpbmNsdWRlIGl0LCBzbyBhZGQgdGhlIFdpblBFLVdNSSBwYWNrYWdlIHdoZW4gcHJlc2VudC4KICAgICRvY0RpciA9ICJDOlxQcm9ncmFtIEZpbGVzICh4ODYpXFdpbmRvd3MgS2l0c1wxMFxBc3Nlc3NtZW50IGFuZCBEZXBsb3ltZW50IEtpdFxXaW5kb3dzIFByZWluc3RhbGxhdGlvbiBFbnZpcm9ubWVudFxhbWQ2NFxXaW5QRV9PQ3MiCiAgICAkd21pQ2FiID0gSm9pbi1QYXRoICRvY0RpciAiV2luUEUtV01JLmNhYiIKICAgICR3bWlMYW5nID0gSm9pbi1QYXRoICRvY0RpciAiZW4tdXNcV2luUEUtV01JX2VuLXVzLmNhYiIKICAgIGlmIChUZXN0LVBhdGggJHdtaUNhYikgewogICAgICAgIFdyaXRlLUhvc3QgIkFkZGluZyBXaW5QRS1XTUkgb3B0aW9uYWwgY29tcG9uZW50Li4uIgogICAgICAgICR3bWlBcmdzID0gQCgiL0FkZC1QYWNrYWdlIiwgIi9JbWFnZTokbW91bnREaXIiLCAiL1BhY2thZ2VQYXRoOiR3bWlDYWIiKQogICAgICAgIGlmIChUZXN0LVBhdGggJHdtaUxhbmcpIHsgJHdtaUFyZ3MgKz0gIi9QYWNrYWdlUGF0aDokd21pTGFuZyIgfQogICAgICAgICYgZGlzbS5leGUgQHdtaUFyZ3MKICAgICAgICBpZiAoJExBU1RFWElUQ09ERSAtbmUgMCkgeyBGYWlsICJESVNNIGFkZCBXaW5QRS1XTUkgZmFpbGVkLiIgfQogICAgfQogICAgZWxzZSB7CiAgICAgICAgV3JpdGUtSG9zdCAiV0FSTklORzogV2luUEUtV01JLmNhYiBub3QgZm91bmQ7IHRoZSB3aXphcmQgZHJpdmUgcGlja2VyIG1heSBub3Qgd29yayBpbiBXaW5QRS4iCiAgICB9CgogICAgIyAtLS0gQWRkIHJlY292ZXJ5IHRvb2xzIC0tLQogICAgJHRvb2xzRGlyID0gSm9pbi1QYXRoICRtb3VudERpciAiZmlsZWtlZXAiCiAgICBOZXctSXRlbSAtSXRlbVR5cGUgRGlyZWN0b3J5IC1Gb3JjZSAtUGF0aCAkdG9vbHNEaXIgfCBPdXQtTnVsbAogICAgV3JpdGUtSG9zdCAiQ29weWluZyBDTEkuLi4iCiAgICBDb3B5LUl0ZW0gKEpvaW4tUGF0aCAkY2xpT3V0ICIqIikgJHRvb2xzRGlyIC1SZWN1cnNlIC1Gb3JjZQogICAgJHdpemFyZERpciA9IEpvaW4tUGF0aCAkdG9vbHNEaXIgIndpemFyZCIKICAgIE5ldy1JdGVtIC1JdGVtVHlwZSBEaXJlY3RvcnkgLUZvcmNlIC1QYXRoICR3aXphcmREaXIgfCBPdXQtTnVsbAogICAgV3JpdGUtSG9zdCAiQ29weWluZyB3aXphcmQuLi4iCiAgICBDb3B5LUl0ZW0gKEpvaW4tUGF0aCAkd2l6T3V0ICIqIikgJHdpemFyZERpciAtUmVjdXJzZSAtRm9yY2UKICAgIFdyaXRlLUhvc3QgIkNvcHlpbmcgcmVjb3ZlcnkgcnVuYm9vay4uLiIKICAgICRydW5ib29rU3JjID0gaWYgKCRCaW5hcnlEaXIgLW5lICIiKSB7IEpvaW4tUGF0aCAkQmluYXJ5RGlyICJkb2NzXFJFQ09WRVJZLm1kIiB9IGAKICAgICAgICAgICAgICAgICAgZWxzZSB7IEpvaW4tUGF0aCAkU291cmNlRGlyICJkb2NzXFJFQ09WRVJZLm1kIiB9CiAgICBpZiAoVGVzdC1QYXRoICRydW5ib29rU3JjKSB7CiAgICAgICAgQ29weS1JdGVtICRydW5ib29rU3JjICR0b29sc0RpciAtRm9yY2UKICAgIH0KICAgIGVsc2UgewogICAgICAgIFdyaXRlLUhvc3QgIldBUk5JTkc6IFJFQ09WRVJZLm1kIG5vdCBmb3VuZCBhdCAkcnVuYm9va1NyYzsgc2tpcHBpbmcuIgogICAgfQoKICAgICMgLS0tIFN0YXJ0dXAgc2NyaXB0OiBuZXR3b3JrIGluaXQgKyBtZW51IC0tLQogICAgJHN0YXJ0bmV0ID0gQCcKd3BlaW5pdApAZWNobyBvZmYKZWNobyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KZWNobyAgVXNlbmV0IEJhY2t1cCBSZWNvdmVyeSBFbnZpcm9ubWVudAplY2hvID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQplY2hvLgplY2hvICBUb29scyBpbiBYOlxmaWxla2VlcFwgOgplY2hvICAgIEZpbGVLZWVwLmV4ZSAgICAgICAgICAgICAgICAgICAoQ0xJKQplY2hvICAgIHdpemFyZFxGaWxlS2VlcFJlY292ZXJ5LmV4ZSAgICAoR1VJIHdpemFyZCkKZWNobyAgICBSRUNPVkVSWS5tZCAgICAgICAgICAgICAgICAgICAgKHJ1bmJvb2spCmVjaG8uCmVjaG8gIFN0YXJ0IG5ldHdvcmtpbmcgaWYgbmVlZGVkOiB3cGVpbml0CmVjaG8gIExhdW5jaGluZyByZWNvdmVyeSB3aXphcmQuLi4Kc3RhcnQgIiIgWDpcZmlsZWtlZXBcd2l6YXJkXEZpbGVLZWVwUmVjb3ZlcnkuZXhlIC0td2luOTUKZWNoby4KY21kCidACiAgICAkc3RhcnRuZXQgfCBPdXQtRmlsZSAtRmlsZVBhdGggKEpvaW4tUGF0aCAkbW91bnREaXIgIldpbmRvd3NcU3lzdGVtMzJcc3RhcnRuZXQuY21kIikgYAogICAgICAgIC1FbmNvZGluZyBhc2NpaSAtRm9yY2UKICAgIFdyaXRlLUhvc3QgInN0YXJ0bmV0LmNtZCB3cml0dGVuLiIKfQpmaW5hbGx5IHsKICAgIFdyaXRlLUhvc3QgIkNvbW1pdHRpbmcgYW5kIHVubW91bnRpbmcuLi4iCiAgICBkaXNtLmV4ZSAvVW5tb3VudC1JbWFnZSAvTW91bnREaXI6JG1vdW50RGlyIC9Db21taXQKICAgIGlmICgkTEFTVEVYSVRDT0RFIC1uZSAwKSB7IEZhaWwgIkRJU00gdW5tb3VudCBmYWlsZWQuIiB9Cn0KCiMgLS0tIEJ1aWxkIElTTyAtLS0KV3JpdGUtSG9zdCAiQnVpbGRpbmcgSVNPLi4uIgpNYWtlV2luUEVNZWRpYS5jbWQgL0lTTyAkV29ya0RpciAkSXNvUGF0aAppZiAoJExBU1RFWElUQ09ERSAtbmUgMCkgeyBGYWlsICJNYWtlV2luUEVNZWRpYSBmYWlsZWQuIiB9CgpXcml0ZS1Ib3N0ICIiCldyaXRlLUhvc3QgIkRvbmU6ICRJc29QYXRoIgpXcml0ZS1Ib3N0ICJXcml0ZSBpdCB0byBVU0Igd2l0aCBSdWZ1cyAob3IgTWFrZVdpblBFTWVkaWEgL1VGRCkuIgo=
"@
[IO.File]::WriteAllBytes("C:\ub\src\install\UsenetBackup.wxs", [Convert]::FromBase64String($wxsB64))
[IO.File]::WriteAllBytes("C:\ub\src\winpe\build-winpe.ps1", [Convert]::FromBase64String($winpeB64))
Write-Host ("  UsenetBackup.wxs: {0:N0} bytes" -f (Get-Item C:\ub\src\install\UsenetBackup.wxs).Length)
Write-Host ("  build-winpe.ps1:  {0:N0} bytes" -f (Get-Item C:\ub\src\winpe\build-winpe.ps1).Length)

Write-Host "=== 2/6 Re-publishing apps as single-file ==="
# Single source root used consistently: the script cds here AND passes it to
# WiX as SrcRoot. (Previously these were two different hardcoded paths,
# Source root: use src-new if it exists (current VM layout), else src.
$srcRoot = if (Test-Path "C:\ub\src-new") { "C:\ub\src-new" } else { "C:\ub\src" }
cd $srcRoot
# Stamp file versions so Windows Installer replaces binaries on upgrade/reinstall.
# (Without this, every build carries the default FileVersion 1.0.0.0 and a
# reinstall silently keeps the previously installed binaries.)
# Build = days since 2026-01-01, revision = minutes since midnight: two builds
# on the same day still get distinct versions, so a same-day rebuild +
# reinstall actually replaces the binaries.
$now = Get-Date
$buildNo = [int](($now - (Get-Date "2026-01-01")).TotalDays)
$revNo = $now.Hour * 60 + $now.Minute
$fileVer = "0.8.$buildNo.$revNo"
$verProps = @("/p:Version=0.8.1", "/p:FileVersion=$fileVer",
              "/p:AssemblyVersion=0.8.1.0", "/p:InformationalVersion=0.8.1")
Write-Host "Stamping FileVersion $fileVer"
dotnet publish src/UsenetBackup.Cli/UsenetBackup.Cli.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\cli /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }
dotnet publish src/UsenetBackup.Service/UsenetBackup.Service.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\service /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }
dotnet publish src/UsenetBackup.Recovery/UsenetBackup.Recovery.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\recovery /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "Recovery publish failed" }

Write-Host "=== 3/6 Verifying required MSI payload files ==="
$required = @(
    "C:\ub\publish\cli\FileKeep.exe",
    "C:\ub\publish\service\FileKeepService.exe",
    "C:\ub\publish\recovery\FileKeepRecovery.exe"
)
foreach ($f in $required) {
    if (Test-Path $f) { Write-Host ("  OK: {0} ({1:N0} bytes)" -f $f, (Get-Item $f).Length) }
    else { throw "MISSING required MSI payload: $f" }
}

Write-Host "=== 4/6 Building MSI ==="
# Sign the executables BEFORE building the MSI so the signed binaries are packaged.
Write-Host "  Signing executables..."
Invoke-CodeSigning -Files @(
    "C:\ub\publish\cli\FileKeep.exe",
    "C:\ub\publish\service\FileKeepService.exe",
    "C:\ub\publish\recovery\FileKeepRecovery.exe"
)

# Build the VSS helper (MSVC or MinGW)
Write-Host "Building VSS helper..."
& "$srcRoot\src\UsenetBackup.VssHelper\build-vss-helper.ps1"
if ($LASTEXITCODE -ne 0) { throw "VSS helper build failed" }

wix build -arch x64 -d SrcRoot=$srcRoot -d CliBin=C:\ub\publish\cli -d ServiceBin=C:\ub\publish\service -d RecoveryBin=C:\ub\publish\recovery -d VssBin=$srcRoot\src\UsenetBackup.VssHelper -o C:\ub\filekeep-0.8.1.msi install/UsenetBackup.wxs
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
$msi = Get-Item C:\ub\filekeep-0.8.1.msi
Write-Host ("  MSI built: {0:N0} bytes" -f $msi.Length)

Write-Host "=== 5/6 Signing MSI ==="
Invoke-CodeSigning -Files @("C:\ub\filekeep-0.8.1.msi")

Write-Host "=== 6/6 Test install + verify + uninstall ==="
Start-Process msiexec -ArgumentList "/i", "C:\ub\filekeep-0.8.1.msi", "/qn" -Wait
Start-Sleep 15
foreach ($c in @("C:\Program Files\FileKeep\cli\FileKeep.exe",
                 "C:\Program Files\FileKeep\service\FileKeepService.exe",
                 "C:\Program Files\FileKeep\wizard\FileKeepRecovery.exe")) {
    if (Test-Path $c) { Write-Host "  OK installed: $c" } else { throw "MISSING after install: $c" }
}
$svc = sc.exe query FileKeep
Write-Host "  Service state:"
Write-Host $svc
if ($svc -notmatch "RUNNING") { Write-Host "  NOTE: service not running after install (expected: registered auto-start, starts on first boot / service start)" }

Write-Host "  Uninstalling..."
# Uninstall by ProductCode from the registered product (not by MSI path),
# because each build generates a new ProductCode.
$prods = @(Get-WmiObject Win32_Product | Where-Object { $_.Name -eq "FileKeep" })
if ($prods.Count -gt 0) {
    foreach ($prod in $prods) {
        Write-Host ("  Uninstalling product: {0}" -f $prod.IdentifyingNumber)
        Start-Process msiexec -ArgumentList "/x", $prod.IdentifyingNumber, "/qn" -Wait
        Start-Sleep 5
    }
    Start-Sleep 5
} else {
    Write-Host "  WARNING: FileKeep not found in Win32_Product, trying MSI path..."
    Start-Process msiexec -ArgumentList "/x", "C:\ub\filekeep-0.8.1.msi", "/qn" -Wait
    Start-Sleep 10
}
if (Test-Path "C:\Program Files\FileKeep") { throw "Uninstall left files behind" }
Write-Host "  Uninstall clean."

Write-Host ""
Write-Host "ALL DONE - MSI built, installed, verified, and uninstalled cleanly."
} finally {
    Stop-Transcript | Out-Null
}
