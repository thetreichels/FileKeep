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
PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTgiPz4KPCEtLSBXaVggdjUgaW5zdGFsbGVyIGZvciBGaWxlS2VlcC4KICAgICBUaGUgYXBwcyBwdWJsaXNoIGFzIHNpbmdsZS1maWxlIGV4ZWN1dGFibGVzIChQdWJsaXNoU2luZ2xlRmlsZSBpbiB0aGUKICAgICBjc3Byb2opLCBzbyB0aGUgcGF5bG9hZCBpcyBhIGhhbmRmdWwgb2YgZXhwbGljaXQgZmlsZXMg4oCUIG5vIGhhcnZlc3RpbmcuCiAgICAgQnVpbGQgb24gV2luZG93cyB3aXRoIHRoZSBXaVggdjUgZG90bmV0IHRvb2w6CiAgICAgICB3aXggYnVpbGQgLWFyY2ggeDY0IC1kIFNyY1Jvb3Q9QzpcdWJcc3JjIC1kIENsaUJpbj1DOlx1YlxwdWJsaXNoXGNsaSAtZCBTZXJ2aWNlQmluPUM6XHViXHB1Ymxpc2hcc2VydmljZSAtZCBSZWNvdmVyeUJpbj1DOlx1YlxwdWJsaXNoXHJlY292ZXJ5IC1vIHVzZW5ldC1iYWNrdXAtMC44LjEteDY0Lm1zaSBpbnN0YWxsL1VzZW5ldEJhY2t1cC53eHMKICAgICBJTVBPUlRBTlQ6IHJlLXB1Ymxpc2ggd2l0aCB0aGUgY3VycmVudCBjc3Byb2ogKHNpbmdsZS1maWxlKSBiZWZvcmUgYnVpbGRpbmc7CiAgICAgdGhlIE1TSSBpbnN0YWxscyBvbmx5IHRoZSBmaWxlcyBsaXN0ZWQgYmVsb3cuCi0tPgo8V2l4IHhtbG5zPSJodHRwOi8vd2l4dG9vbHNldC5vcmcvc2NoZW1hcy92NC93eHMiPgogIDxQYWNrYWdlIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgIFZlcnNpb249IjAuOC4xIgogICAgICAgICAgIE1hbnVmYWN0dXJlcj0iSi4gVHJlaWNoZWwiCiAgICAgICAgICAgVXBncmFkZUNvZGU9IkY2RTBENkQxLTYxQTEtNDg0My04N0I5LTI4MjVBNDdBMThERCIKICAgICAgICAgICBTY29wZT0icGVyTWFjaGluZSI+CgogICAgPE1ham9yVXBncmFkZSBEb3duZ3JhZGVFcnJvck1lc3NhZ2U9IkEgbmV3ZXIgdmVyc2lvbiBvZiBGaWxlS2VlcCBpcyBhbHJlYWR5IGluc3RhbGxlZC4iIC8+CgogICAgPE1lZGlhIElkPSIxIiBDYWJpbmV0PSJwcm9kdWN0LmNhYiIgRW1iZWRDYWI9InllcyIgLz4KCiAgICA8U3RhbmRhcmREaXJlY3RvcnkgSWQ9IlByb2dyYW1GaWxlczY0MzJGb2xkZXIiPgogICAgICA8RGlyZWN0b3J5IElkPSJJTlNUQUxMRk9MREVSIiBOYW1lPSJGaWxlS2VlcCI+CiAgICAgICAgPERpcmVjdG9yeSBJZD0iQ0xJRElSIiBOYW1lPSJjbGkiIC8+CiAgICAgICAgPERpcmVjdG9yeSBJZD0iU0VSVklDRURJUiIgTmFtZT0ic2VydmljZSIgLz4KICAgICAgICA8RGlyZWN0b3J5IElkPSJXSVpBUkRESVIiIE5hbWU9IndpemFyZCIgLz4KICAgICAgICA8RGlyZWN0b3J5IElkPSJET0NTRElSIiBOYW1lPSJkb2NzIiAvPgogICAgICA8L0RpcmVjdG9yeT4KICAgIDwvU3RhbmRhcmREaXJlY3Rvcnk+CgogICAgPCEtLSBDTEkgKHNpbmdsZS1maWxlIGV4ZTsgbmF0aXZlIFNRTGl0ZSBpcyBidW5kbGVkIGluc2lkZSkgLS0+CiAgICA8Q29tcG9uZW50R3JvdXAgSWQ9IkNsaUZpbGVzIiBEaXJlY3Rvcnk9IkNMSURJUiI+CiAgICAgIDxDb21wb25lbnQgSWQ9IkNsaUV4ZSIgR3VpZD0iKiI+CiAgICAgICAgPEZpbGUgSWQ9IkNsaUV4ZUZpbGUiIFNvdXJjZT0iJCh2YXIuQ2xpQmluKVxGaWxlS2VlcC5leGUiIEtleVBhdGg9InllcyIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlQ2xpUGRiIiBEaXJlY3Rvcnk9IkNMSURJUiIgTmFtZT0iKi5wZGIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgICAgPFJlbW92ZUZvbGRlciBJZD0iUmVtb3ZlQ2xpRGlyIiBEaXJlY3Rvcnk9IkNMSURJUiIgT249InVuaW5zdGFsbCIgLz4KICAgICAgPC9Db21wb25lbnQ+CiAgICA8L0NvbXBvbmVudEdyb3VwPgoKICAgIDxDb21wb25lbnRHcm91cCBJZD0iU2VydmljZVJlZ2lzdHJhdGlvbiIgRGlyZWN0b3J5PSJTRVJWSUNFRElSIj4KICAgICAgPENvbXBvbmVudCBJZD0iU2VydmljZUV4ZWN1dGFibGUiPgogICAgICAgIDxGaWxlIElkPSJTZXJ2aWNlRXhlIiBTb3VyY2U9IiQodmFyLlNlcnZpY2VCaW4pXEZpbGVLZWVwU2VydmljZS5leGUiIEtleVBhdGg9InllcyIgLz4KICAgICAgICA8U2VydmljZUluc3RhbGwgSWQ9IkJhY2t1cFNlcnZpY2UiCiAgICAgICAgICAgICAgICAgICAgICAgIFR5cGU9Im93blByb2Nlc3MiCiAgICAgICAgICAgICAgICAgICAgICAgIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgICAgICAgICAgICAgICBEaXNwbGF5TmFtZT0iRmlsZUtlZXAgU2VydmljZSIKICAgICAgICAgICAgICAgICAgICAgICAgRGVzY3JpcHRpb249IlNjaGVkdWxlZCBlbmNyeXB0ZWQgYmFja3VwcyB0byBVc2VuZXQuIgogICAgICAgICAgICAgICAgICAgICAgICBTdGFydD0iYXV0byIKICAgICAgICAgICAgICAgICAgICAgICAgQWNjb3VudD0iTG9jYWxTeXN0ZW0iCiAgICAgICAgICAgICAgICAgICAgICAgIEVycm9yQ29udHJvbD0ibm9ybWFsIiAvPgogICAgICAgIDxTZXJ2aWNlQ29udHJvbCBJZD0iQmFja3VwU2VydmljZUNvbnRyb2wiCiAgICAgICAgICAgICAgICAgICAgICAgIE5hbWU9IkZpbGVLZWVwIgogICAgICAgICAgICAgICAgICAgICAgICBTdG9wPSJib3RoIgogICAgICAgICAgICAgICAgICAgICAgICBSZW1vdmU9InVuaW5zdGFsbCIKICAgICAgICAgICAgICAgICAgICAgICAgV2FpdD0idHJ1ZSIgLz4KICAgICAgICA8IS0tIENsZWFuIHVwIHJ1bnRpbWUtY3JlYXRlZCBmaWxlcyAoc2VydmljZSB3cml0ZXMgY29uZmlnL2xvZ3MgaGVyZSwgc2luZ2xlLWZpbGUgZXhlIGV4dHJhY3RzIG5hdGl2ZSBsaWJzKSAtLT4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlU2VydmljZUpzb24iIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0ic2VydmljZS5qc29uIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVTZXJ2aWNlTG9nIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE5hbWU9InNlcnZpY2UubG9nIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVTcWxpdGVEbGwiIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0iZV9zcWxpdGUzLmRsbCIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlU2VydmljZVBkYiIgRGlyZWN0b3J5PSJTRVJWSUNFRElSIiBOYW1lPSIqLnBkYiIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRmlsZSBJZD0iUmVtb3ZlQXNwTmV0RGxsIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE5hbWU9ImFzcG5ldGNvcmV2Ml9pbnByb2Nlc3MuZGxsIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICAgIDxSZW1vdmVGaWxlIElkPSJSZW1vdmVXZWJDb25maWciIERpcmVjdG9yeT0iU0VSVklDRURJUiIgTmFtZT0id2ViLmNvbmZpZyIgT249InVuaW5zdGFsbCIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVTZXJ2aWNlRGlyIiBEaXJlY3Rvcnk9IlNFUlZJQ0VESVIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgIDwvQ29tcG9uZW50PgogICAgPC9Db21wb25lbnRHcm91cD4KCiAgICA8IS0tIFJlY292ZXJ5IHdpemFyZCAoc2luZ2xlLWZpbGUgZXhlOyBuYXRpdmUgU1FMaXRlIGlzIGJ1bmRsZWQgaW5zaWRlKSAtLT4KICAgIDxDb21wb25lbnRHcm91cCBJZD0iUmVjb3ZlcnlGaWxlcyIgRGlyZWN0b3J5PSJXSVpBUkRESVIiPgogICAgICA8Q29tcG9uZW50IElkPSJSZWNvdmVyeUV4ZSIgR3VpZD0iKiI+CiAgICAgICAgPEZpbGUgSWQ9IlJlY292ZXJ5RXhlRmlsZSIgU291cmNlPSIkKHZhci5SZWNvdmVyeUJpbilcRmlsZUtlZXBSZWNvdmVyeS5leGUiIEtleVBhdGg9InllcyIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVXaXphcmREaXIiIERpcmVjdG9yeT0iV0laQVJERElSIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICA8L0NvbXBvbmVudD4KICAgIDwvQ29tcG9uZW50R3JvdXA+CgogICAgPCEtLSBTZXJ2aWNlIGNvbmZpZyBleGFtcGxlICsgcmVjb3ZlcnkgcnVuYm9vayAtLT4KICAgIDxDb21wb25lbnRHcm91cCBJZD0iRG9jcyIgRGlyZWN0b3J5PSJET0NTRElSIj4KICAgICAgPENvbXBvbmVudCBJZD0iRG9jc0NvbXBvbmVudCIgR3VpZD0iNDM2MzFGMDAtMjZDQi00NkE3LUJEOEYtMzhEQjQ0RUM0QTU0Ij4KICAgICAgICA8RmlsZSBTb3VyY2U9IiQodmFyLlNyY1Jvb3QpXHNyY1xVc2VuZXRCYWNrdXAuU2VydmljZVxzZXJ2aWNlLmV4YW1wbGUuanNvbiIgTmFtZT0ic2VydmljZS5leGFtcGxlLmpzb24iIC8+CiAgICAgICAgPEZpbGUgU291cmNlPSIkKHZhci5TcmNSb290KVxkb2NzXFJFQ09WRVJZLm1kIiBOYW1lPSJSRUNPVkVSWS5tZCIgLz4KICAgICAgICA8UmVtb3ZlRm9sZGVyIElkPSJSZW1vdmVEb2NzRGlyIiBEaXJlY3Rvcnk9IkRPQ1NESVIiIE9uPSJ1bmluc3RhbGwiIC8+CiAgICAgICAgPFJlbW92ZUZvbGRlciBJZD0iUmVtb3ZlSW5zdGFsbERpciIgRGlyZWN0b3J5PSJJTlNUQUxMRk9MREVSIiBPbj0idW5pbnN0YWxsIiAvPgogICAgICA8L0NvbXBvbmVudD4KICAgIDwvQ29tcG9uZW50R3JvdXA+CgogICAgPEZlYXR1cmUgSWQ9IlByb2R1Y3RGZWF0dXJlIiBUaXRsZT0iRmlsZUtlZXAiIExldmVsPSIxIj4KICAgICAgPENvbXBvbmVudEdyb3VwUmVmIElkPSJDbGlGaWxlcyIgLz4KICAgICAgPENvbXBvbmVudEdyb3VwUmVmIElkPSJTZXJ2aWNlUmVnaXN0cmF0aW9uIiAvPgogICAgICA8Q29tcG9uZW50R3JvdXBSZWYgSWQ9IlJlY292ZXJ5RmlsZXMiIC8+CiAgICAgIDxDb21wb25lbnRHcm91cFJlZiBJZD0iRG9jcyIgLz4KICAgIDwvRmVhdHVyZT4KCiAgICA8IS0tIEFkZC9SZW1vdmUgUHJvZ3JhbXMgaWNvbiAtLT4KICAgIDxJY29uIElkPSJGaWxlS2VlcEljb24iIFNvdXJjZUZpbGU9IiQodmFyLlNyY1Jvb3QpL2Fzc2V0cy9maWxla2VlcC5pY28iIC8+CiAgICA8UHJvcGVydHkgSWQ9IkFSUFBST0RVQ1RJQ09OIiBWYWx1ZT0iRmlsZUtlZXBJY29uIiAvPgogIDwvUGFja2FnZT4KPC9XaXg+Cg==
"@
$winpeB64 = @"
IyBCdWlsZHMgYSBXaW5QRSBJU08gd2l0aCB0aGUgVXNlbmV0IEJhY2t1cCByZWNvdmVyeSB0b29s
cy4KIyBSdW4gb24gV2luZG93cyB3aXRoIHRoZSBBREsgKyBXaW5QRSBhZGQtb24gaW5zdGFsbGVk
LCBhcyBBZG1pbmlzdHJhdG9yLgojCiMgICAxLiBJbnN0YWxsIEFESyAoRGVwbG95bWVudCBUb29s
cykgKyBXaW5QRSBhZGQtb24gZnJvbQojICAgICAgaHR0cHM6Ly9sZWFybi5taWNyb3NvZnQuY29t
L2VuLXVzL3dpbmRvd3MtaGFyZHdhcmUvZ2V0LXN0YXJ0ZWQvYWRrLWluc3RhbGwvCiMgICAyLiBk
b3RuZXQgcHVibGlzaCB0aGUgQ0xJIGFuZCB3aXphcmQgKHNlbGYtY29udGFpbmVkLCB3aW4teDY0
KQojICAgMy4gUnVuIHRoaXMgc2NyaXB0OiAuXHdpbnBlXGJ1aWxkLXdpbnBlLnBzMSAtU291cmNl
RGlyIEM6XHViXHNyYwojCiMgT3V0cHV0OiBDOlx3aW5wZVx1c2VuZXQtYmFja3VwLXdpbnBlLmlz
bwoKcGFyYW0oCiAgICBbc3RyaW5nXSRTb3VyY2VEaXIgPSAiQzpcdWJcc3JjIiwKICAgIFtzdHJp
bmddJFdvcmtEaXIgPSAiQzpcd2lucGUiLAogICAgW3N0cmluZ10kU3RhZ2VEaXIgPSAiQzpcd2lu
cGUtc3RhZ2UiLAogICAgW3N0cmluZ10kSXNvUGF0aCA9ICJDOlx3aW5wZVx1c2VuZXQtYmFja3Vw
LXdpbnBlLmlzbyIKKQoKJEVycm9yQWN0aW9uUHJlZmVyZW5jZSA9ICJTdG9wIgoKZnVuY3Rpb24g
RmFpbCgkbXNnKSB7IFdyaXRlLUVycm9yICRtc2c7IGV4aXQgMSB9CgojIC0tLSBQcmVjb25kaXRp
b25zIC0tLQppZiAoLW5vdCAoW1NlY3VyaXR5LlByaW5jaXBhbC5XaW5kb3dzUHJpbmNpcGFsXVtT
ZWN1cml0eS5QcmluY2lwYWwuV2luZG93c0lkZW50aXR5XTo6R2V0Q3VycmVudCgpKS5Jc0luUm9s
ZShbU2VjdXJpdHkuUHJpbmNpcGFsLldpbmRvd3NCdWlsdEluUm9sZV06OkFkbWluaXN0cmF0b3Ip
KSB7CiAgICBGYWlsICJSdW4gYXMgQWRtaW5pc3RyYXRvci4iCn0KIyBUaGUgQURLIGluc3RhbGxl
ciBkb2VzIG5vdCBwdXQgRGVwbG95bWVudCBUb29scyBvbiBQQVRILCBhbmQgdGhlIHRvb2xzIGFy
ZQojIG5vdCBhbHdheXMgYXQgdGhlIHRleHRib29rIGxvY2F0aW9uLCBzbyBwcm9iZSB0aGUgc3Rh
bmRhcmQga2l0IGRpcnMsIHRoZW4KIyBQQVRILCB0aGVuIHNlYXJjaCB0aGUga2l0IHRyZWUgKHNt
YWxsKSBiZWZvcmUgZ2l2aW5nIHVwLgpmdW5jdGlvbiBGaW5kLUFka1Rvb2woW3N0cmluZ10kbmFt
ZSkgewogICAgJGNtZCA9IEdldC1Db21tYW5kICRuYW1lIC1FcnJvckFjdGlvbiBTaWxlbnRseUNv
bnRpbnVlCiAgICBpZiAoJGNtZCkgeyByZXR1cm4gJGNtZC5Tb3VyY2UgfQogICAgJGtpdFJvb3Rz
ID0gQCgKICAgICAgICAiQzpcUHJvZ3JhbSBGaWxlcyAoeDg2KVxXaW5kb3dzIEtpdHNcMTBcQXNz
ZXNzbWVudCBhbmQgRGVwbG95bWVudCBLaXQiLAogICAgICAgICJDOlxQcm9ncmFtIEZpbGVzXFdp
bmRvd3MgS2l0c1wxMFxBc3Nlc3NtZW50IGFuZCBEZXBsb3ltZW50IEtpdCIKICAgICkKICAgIGZv
cmVhY2ggKCRyb290IGluICRraXRSb290cykgewogICAgICAgICRwcm9iZSA9IEpvaW4tUGF0aCAk
cm9vdCAiRGVwbG95bWVudCBUb29sc1wkbmFtZSIKICAgICAgICBpZiAoVGVzdC1QYXRoICRwcm9i
ZSkgeyByZXR1cm4gJHByb2JlIH0KICAgIH0KICAgIGZvcmVhY2ggKCRyb290IGluICRraXRSb290
cykgewogICAgICAgIGlmIChUZXN0LVBhdGggJHJvb3QpIHsKICAgICAgICAgICAgJGZvdW5kID0g
R2V0LUNoaWxkSXRlbSAkcm9vdCAtRmlsdGVyICRuYW1lIC1SZWN1cnNlIC1FcnJvckFjdGlvbiBT
aWxlbnRseUNvbnRpbnVlIHwKICAgICAgICAgICAgICAgICAgICAgU2VsZWN0LU9iamVjdCAtRmly
c3QgMSAtRXhwYW5kUHJvcGVydHkgRnVsbE5hbWUKICAgICAgICAgICAgaWYgKCRmb3VuZCkgeyBy
ZXR1cm4gJGZvdW5kIH0KICAgICAgICB9CiAgICB9CiAgICByZXR1cm4gJG51bGwKfQpmb3JlYWNo
ICgkdG9vbCBpbiBAKCJjb3B5cGUuY21kIiwgIk1ha2VXaW5QRU1lZGlhLmNtZCIpKSB7CiAgICAk
dG9vbFBhdGggPSBGaW5kLUFka1Rvb2wgJHRvb2wKICAgIGlmICgtbm90ICR0b29sUGF0aCkgeyBG
YWlsICIkdG9vbCBub3QgZm91bmQuIEluc3RhbGwgQURLIERlcGxveW1lbnQgVG9vbHMgKyBXaW5Q
RSBhZGQtb24uIiB9CiAgICAkdG9vbERpciA9IFNwbGl0LVBhdGggJHRvb2xQYXRoCiAgICBpZiAo
JGVudjpQQVRIIC1ub3RsaWtlICIqJHRvb2xEaXIqIikgeyAkZW52OlBBVEggPSAiJHRvb2xEaXI7
JGVudjpQQVRIIiB9CiAgICBXcml0ZS1Ib3N0ICIkdG9vbCAtPiAkdG9vbFBhdGgiCn0KaWYgKC1u
b3QgKEdldC1Db21tYW5kIGRpc20uZXhlIC1FcnJvckFjdGlvbiBTaWxlbnRseUNvbnRpbnVlKSkg
eyBGYWlsICJkaXNtLmV4ZSBub3QgZm91bmQuIiB9CgojIC0tLSBQdWJsaXNoIHNlbGYtY29udGFp
bmVkIGJpbmFyaWVzIC0tLQojIE5PVEU6IHB1Ymxpc2ggaW50byBTdGFnZURpciwgTk9UIFdvcmtE
aXIg4oCUIFdvcmtEaXIgaXMgd2lwZWQgYmVsb3cgYnkgY29weXBlLgpXcml0ZS1Ib3N0ICJQdWJs
aXNoaW5nIENMSSBhbmQgcmVjb3Zlcnkgd2l6YXJkIChzZWxmLWNvbnRhaW5lZCB3aW4teDY0KS4u
LiIKJGNsaU91dCA9IEpvaW4tUGF0aCAkU3RhZ2VEaXIgImNsaSIKJHdpek91dCA9IEpvaW4tUGF0
aCAkU3RhZ2VEaXIgInJlY292ZXJ5Igpkb3RuZXQgcHVibGlzaCAoSm9pbi1QYXRoICRTb3VyY2VE
aXIgInNyY1xVc2VuZXRCYWNrdXAuQ2xpXFVzZW5ldEJhY2t1cC5DbGkuY3Nwcm9qIikgYAogICAg
LWMgUmVsZWFzZSAtciB3aW4teDY0IC0tc2VsZi1jb250YWluZWQgLW8gJGNsaU91dAppZiAoJExB
U1RFWElUQ09ERSAtbmUgMCkgeyBGYWlsICJDTEkgcHVibGlzaCBmYWlsZWQuIiB9CmRvdG5ldCBw
dWJsaXNoIChKb2luLVBhdGggJFNvdXJjZURpciAic3JjXFVzZW5ldEJhY2t1cC5SZWNvdmVyeVxV
c2VuZXRCYWNrdXAuUmVjb3ZlcnkuY3Nwcm9qIikgYAogICAgLWMgUmVsZWFzZSAtciB3aW4teDY0
IC0tc2VsZi1jb250YWluZWQgLW8gJHdpek91dAppZiAoJExBU1RFWElUQ09ERSAtbmUgMCkgeyBG
YWlsICJXaXphcmQgcHVibGlzaCBmYWlsZWQuIiB9CgojIC0tLSBCdWlsZCBXaW5QRSBiYXNlIC0t
LQppZiAoVGVzdC1QYXRoICRXb3JrRGlyKSB7IFJlbW92ZS1JdGVtICRXb3JrRGlyIC1SZWN1cnNl
IC1Gb3JjZSB9CldyaXRlLUhvc3QgIlJ1bm5pbmcgY29weXBlLi4uIgpjb3B5cGUuY21kIGFtZDY0
ICRXb3JrRGlyCmlmICgkTEFTVEVYSVRDT0RFIC1uZSAwKSB7IEZhaWwgImNvcHlwZSBmYWlsZWQu
IiB9CgokbW91bnREaXIgPSBKb2luLVBhdGggJFdvcmtEaXIgIm1vdW50IgokYm9vdFdpbSA9IEpv
aW4tUGF0aCAkV29ya0RpciAibWVkaWFcc291cmNlc1xib290LndpbSIKCldyaXRlLUhvc3QgIk1v
dW50aW5nIGJvb3Qud2ltLi4uIgpkaXNtLmV4ZSAvTW91bnQtSW1hZ2UgL0ltYWdlRmlsZTokYm9v
dFdpbSAvaW5kZXg6MSAvTW91bnREaXI6JG1vdW50RGlyCmlmICgkTEFTVEVYSVRDT0RFIC1uZSAw
KSB7IEZhaWwgIkRJU00gbW91bnQgZmFpbGVkLiIgfQoKdHJ5IHsKICAgICMgLS0tIFdpblBFIG9w
dGlvbmFsIGNvbXBvbmVudHMgLS0tCiAgICAjIFRoZSB3aXphcmQncyBkcml2ZSBwaWNrZXIgdXNl
cyBXTUkgKFN5c3RlbS5NYW5hZ2VtZW50KTsgdGhlIGJhc2UgV2luUEUKICAgICMgaW1hZ2UgbWF5
IG5vdCBpbmNsdWRlIGl0LCBzbyBhZGQgdGhlIFdpblBFLVdNSSBwYWNrYWdlIHdoZW4gcHJlc2Vu
dC4KICAgICRvY0RpciA9ICJDOlxQcm9ncmFtIEZpbGVzICh4ODYpXFdpbmRvd3MgS2l0c1wxMFxB
c3Nlc3NtZW50IGFuZCBEZXBsb3ltZW50IEtpdFxXaW5kb3dzIFByZWluc3RhbGxhdGlvbiBFbnZp
cm9ubWVudFxhbWQ2NFxXaW5QRV9PQ3MiCiAgICAkd21pQ2FiID0gSm9pbi1QYXRoICRvY0RpciAi
V2luUEUtV01JLmNhYiIKICAgICR3bWlMYW5nID0gSm9pbi1QYXRoICRvY0RpciAiZW4tdXNcV2lu
UEUtV01JX2VuLXVzLmNhYiIKICAgIGlmIChUZXN0LVBhdGggJHdtaUNhYikgewogICAgICAgIFdy
aXRlLUhvc3QgIkFkZGluZyBXaW5QRS1XTUkgb3B0aW9uYWwgY29tcG9uZW50Li4uIgogICAgICAg
ICR3bWlBcmdzID0gQCgiL0FkZC1QYWNrYWdlIiwgIi9JbWFnZTokbW91bnREaXIiLCAiL1BhY2th
Z2VQYXRoOiR3bWlDYWIiKQogICAgICAgIGlmIChUZXN0LVBhdGggJHdtaUxhbmcpIHsgJHdtaUFy
Z3MgKz0gIi9QYWNrYWdlUGF0aDokd21pTGFuZyIgfQogICAgICAgICYgZGlzbS5leGUgQHdtaUFy
Z3MKICAgICAgICBpZiAoJExBU1RFWElUQ09ERSAtbmUgMCkgeyBGYWlsICJESVNNIGFkZCBXaW5Q
RS1XTUkgZmFpbGVkLiIgfQogICAgfQogICAgZWxzZSB7CiAgICAgICAgV3JpdGUtSG9zdCAiV0FS
TklORzogV2luUEUtV01JLmNhYiBub3QgZm91bmQ7IHRoZSB3aXphcmQgZHJpdmUgcGlja2VyIG1h
eSBub3Qgd29yayBpbiBXaW5QRS4iCiAgICB9CgogICAgIyAtLS0gQWRkIHJlY292ZXJ5IHRvb2xz
IC0tLQogICAgJHRvb2xzRGlyID0gSm9pbi1QYXRoICRtb3VudERpciAidXNlbmV0LWJhY2t1cCIK
ICAgIE5ldy1JdGVtIC1JdGVtVHlwZSBEaXJlY3RvcnkgLUZvcmNlIC1QYXRoICR0b29sc0RpciB8
IE91dC1OdWxsCiAgICBXcml0ZS1Ib3N0ICJDb3B5aW5nIENMSS4uLiIKICAgIENvcHktSXRlbSAo
Sm9pbi1QYXRoICRjbGlPdXQgIioiKSAkdG9vbHNEaXIgLVJlY3Vyc2UgLUZvcmNlCiAgICAkd2l6
YXJkRGlyID0gSm9pbi1QYXRoICR0b29sc0RpciAid2l6YXJkIgogICAgTmV3LUl0ZW0gLUl0ZW1U
eXBlIERpcmVjdG9yeSAtRm9yY2UgLVBhdGggJHdpemFyZERpciB8IE91dC1OdWxsCiAgICBXcml0
ZS1Ib3N0ICJDb3B5aW5nIHdpemFyZC4uLiIKICAgIENvcHktSXRlbSAoSm9pbi1QYXRoICR3aXpP
dXQgIioiKSAkd2l6YXJkRGlyIC1SZWN1cnNlIC1Gb3JjZQogICAgV3JpdGUtSG9zdCAiQ29weWlu
ZyByZWNvdmVyeSBydW5ib29rLi4uIgogICAgQ29weS1JdGVtIChKb2luLVBhdGggJFNvdXJjZURp
ciAiZG9jc1xSRUNPVkVSWS5tZCIpICR0b29sc0RpciAtRm9yY2UKCiAgICAjIC0tLSBTdGFydHVw
IHNjcmlwdDogbmV0d29yayBpbml0ICsgbWVudSAtLS0KICAgICRzdGFydG5ldCA9IEAnCndwZWlu
aXQKQGVjaG8gb2ZmCmVjaG8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09
PT09PT09PT09CmVjaG8gIFVzZW5ldCBCYWNrdXAgUmVjb3ZlcnkgRW52aXJvbm1lbnQKZWNobyA9
PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KZWNoby4KZWNo
byAgVG9vbHMgaW4gWDpcdXNlbmV0LWJhY2t1cFwgOgplY2hvICAgIHVzZW5ldC1iYWNrdXAuZXhl
ICAgICAgICAgICAoQ0xJKQplY2hvICAgIHdpemFyZFx1c2VuZXQtYmFja3VwLXJlY292ZXJ5LmV4
ZSAgKEdVSSB3aXphcmQpCmVjaG8gICAgUkVDT1ZFUlkubWQgICAgICAgICAgICAgICAgIChydW5i
b29rKQplY2hvLgplY2hvICBTdGFydCBuZXR3b3JraW5nIGlmIG5lZWRlZDogd3BlaW5pdAplY2hv
ICBMYXVuY2hpbmcgcmVjb3Zlcnkgd2l6YXJkLi4uCnN0YXJ0ICIiIFg6XHVzZW5ldC1iYWNrdXBc
d2l6YXJkXHVzZW5ldC1iYWNrdXAtcmVjb3ZlcnkuZXhlIC0td2luOTUKZWNoby4KY21kCidACiAg
ICAkc3RhcnRuZXQgfCBPdXQtRmlsZSAtRmlsZVBhdGggKEpvaW4tUGF0aCAkbW91bnREaXIgIldp
bmRvd3NcU3lzdGVtMzJcc3RhcnRuZXQuY21kIikgYAogICAgICAgIC1FbmNvZGluZyBhc2NpaSAt
Rm9yY2UKICAgIFdyaXRlLUhvc3QgInN0YXJ0bmV0LmNtZCB3cml0dGVuLiIKfQpmaW5hbGx5IHsK
ICAgIFdyaXRlLUhvc3QgIkNvbW1pdHRpbmcgYW5kIHVubW91bnRpbmcuLi4iCiAgICBkaXNtLmV4
ZSAvVW5tb3VudC1JbWFnZSAvTW91bnREaXI6JG1vdW50RGlyIC9Db21taXQKICAgIGlmICgkTEFT
VEVYSVRDT0RFIC1uZSAwKSB7IEZhaWwgIkRJU00gdW5tb3VudCBmYWlsZWQuIiB9Cn0KCiMgLS0t
IEJ1aWxkIElTTyAtLS0KV3JpdGUtSG9zdCAiQnVpbGRpbmcgSVNPLi4uIgpNYWtlV2luUEVNZWRp
YS5jbWQgL0lTTyAkV29ya0RpciAkSXNvUGF0aAppZiAoJExBU1RFWElUQ09ERSAtbmUgMCkgeyBG
YWlsICJNYWtlV2luUEVNZWRpYSBmYWlsZWQuIiB9CgpXcml0ZS1Ib3N0ICIiCldyaXRlLUhvc3Qg
IkRvbmU6ICRJc29QYXRoIgpXcml0ZS1Ib3N0ICJXcml0ZSBpdCB0byBVU0Igd2l0aCBSdWZ1cyAo
b3IgTWFrZVdpblBFTWVkaWEgL1VGRCkuIgo=
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
Start-Process msiexec -ArgumentList "/x", "C:\ub\filekeep-0.8.1.msi", "/qn" -Wait
Start-Sleep 10
if (Test-Path "C:\Program Files\FileKeep") { throw "Uninstall left files behind" }
Write-Host "  Uninstall clean."

Write-Host ""
Write-Host "ALL DONE - MSI built, installed, verified, and uninstalled cleanly."
} finally {
    Stop-Transcript | Out-Null
}
