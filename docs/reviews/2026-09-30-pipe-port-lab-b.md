# Review: request-changes

Reviewed eeb92ef (the working tree is cfbbd0e, which only edits docs/handoff.md, so the code is identical). The core of the change — PrintPipeListener's instance lifecycle (first-instance take, spare, re-take after all instances close, Stop grace, cancellation and dispose ordering), the EofCounter/acceptance rule, the DACL, and the tracker/listener hand-off through jobs\ — holds up under reading: I traced every exit from ServeAsync/RunAsync and found no path that loses a connection or raises NewPrintJob twice, and the pipe mask really is 0x13019b as the spec claims. The problems are at the edges: the installer's rescue of a leftover print.pdf is invisible and unretried when it fails, a whole job whose rename fails is quietly discarded at the next start, the PS 5.1 arg-line test was not updated for the new -PipeName parameter, and the tray has no health check for the listener itself once the 10-minute stuck-job window closes. Note: some of my initial file reads came back silently truncated; I re-read PrintPipeListener.cs, TrayContext.cs, JobTracker.cs and PrintQueueJobs.cs to completion and cross-checked line numbers with ripgrep, and I verified JOB_INFO_1.Submitted is documented as UTC (so treating it as UTC is correct, not a bug).

## Findings

| severity | location | title |
|---|---|---|
| medium | client/LANFax.App/Logic/PrinterSetup.cs:148 | Migration warnings are thrown away; a print.pdf the script failed to rescue is orphaned forever and the tray says "installed" |
| medium | client/LANFax.Core/PrintPipeListener.cs:453 | A fully received job whose rename fails is swept to rejected\ at the next start and never offered as unsent |
| medium | client/LANFax.Tests/InstallScriptTests.cs:90 | The PS 5.1 arg-line round-trip test was not updated for the -PipeName this commit added to both scripts |
| medium | client/LANFax.App/TrayContext.cs:199 | Nothing checks the listener after the 10-minute window: a dead pipe server looks healthy forever |
| low | client/LANFax.Core/PrintPipeListener.cs:203 | A concurrent Stop returns immediately, so the session-end stop can be a no-op |
| low | client/LANFax.Core/PrintQueueJobs.cs:22 | StuckSince also flags jobs the spooler is still retrying, and the balloon invites a restart that duplicates a fax |
| low | client/LANFax.Core/PrintPipeListener.cs:230 | Start reports our own lingering listener as a squatter |
| low | client/tools/PipeProbe/Program.cs:140 | The -Same probe reports FAIL for the outcome the spec records as normal |
| note | docs/client-spec.md:100 | Spec says the rescued print.pdf is only offered at tray start; the code also offers it right after an install |

### [medium] client/LANFax.App/Logic/PrinterSetup.cs:148 - Migration warnings are thrown away; a print.pdf the script failed to rescue is orphaned forever and the tray says "installed"

InstallAsync decides success purely from a registry read: `var st = Status(printerName); ... PrinterState.Installed => null`. RunProcessAsync (line 157) starts powershell with `UseShellExecute = true, WindowStyle = Hidden, Verb = "runas"`, so stdout cannot be redirected and is never read, and the exit code is never inspected. The install script's whole rescue step is wrapped in a warning-only catch — Install-LANFaxPrinter.ps1:177 `catch { "warning: moving the document left in '$oldPort' out of the way failed (move it into the jobs or rejected folder by hand): ..." }` — and Test-SinglePdf does `[IO.File]::ReadAllBytes($Path)` on a file the spooler may still be writing (line 66-71), which throws IOException and lands in exactly that catch. Once the printer has been moved to the pipe port (script line 118), nothing in the product reads spool\print.pdf any more: grep over client/ finds only the installer and one doc comment. So the user gets the balloon "The \"LAN Fax\" printer is installed and prints to \\.\pipe\LANFax." (TrayContext.cs:563), the document stays in print.pdf, and because the port now matches, PrinterWatch never announces anything and the rescue block is never re-run. The Inno path is no better (LANFax.iss:69 runs it SW_HIDE and only checks ResultCode). docs/client-spec.md:96 leans on "in the script output" for these reports, but neither launcher shows that output.

**Fix:** Capture the script's text (e.g. UseShellExecute=false plus CREATE_NO_WINDOW, or -Command "& script ... *>&1 | Tee-Content <temp>") and fold any line starting with "warning:" into the tray's result balloon and FileLog; also treat a non-zero exit code as a failure even when the port already reads correctly, and re-run the rescue when spool\print.pdf still exists at tray start.

### [medium] client/LANFax.Core/PrintPipeListener.cs:453 - A fully received job whose rename fails is swept to rejected\ at the next start and never offered as unsent

After the job is complete and flushed, `try { MoveFile(part, dest); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report("a print job arrived but could not be renamed; it is kept as {part}...", ...); return; }`. MoveFile (line 487) only retries for ~1 s against a scanner holding the just-written file, so this is reachable. The bytes are then sitting in jobs\<guid>.pdf.part — a name the tracker does not scan (JobTracker.Scan only enumerates "*.json" and "*.pdf"), so it is never offered as unsent in this session. And the queue job is already gone: the spooler's write succeeded, so it never enters JOB_STATUS_ERROR and the stuck-jobs check cannot point at it either. At the next Start, MoveStaleParts (line 224-230) moves it to rejected\ and logs it as being "from an interrupted job", which it was not. Net effect: a document that arrived whole is discarded, with only one earlier balloon as the trace.

**Fix:** On a rename failure, don't leave the complete job under the .part extension — either copy it to jobs\<guid>.pdf (so JobTracker.Scan/NewDocuments offer it), or mark it so MoveStaleParts leaves it alone and the next start offers it; and reword the stale-sweep log for that case.

### [medium] client/LANFax.Tests/InstallScriptTests.cs:90 - The PS 5.1 arg-line round-trip test was not updated for the -PipeName this commit added to both scripts

The generated driver declares `param([string]$DataDir, [string]$SpoolDir, [string]$PrinterName)` and the test feeds back only those three values, but the test extracts the script's own `$argLine` line verbatim (line 79) and this commit appended `-PipeName $(ConvertTo-QuotedArg $PipeName)` to it in both scripts (Install-LANFaxPrinter.ps1:33, Uninstall-LANFaxPrinter.ps1:29; see the `ScriptsUseThePipePortNamedByPipeName` assertion for `"-PipeName $(ConvertTo-QuotedArg $PipeName)"`). Inside the driver `$PipeName` is therefore undefined, so the child is launched as `powershell -File <driver> ... -PipeName <empty>` against a script that has no such parameter — a parameter-binding failure, so `Assert.True(p.ExitCode == 0 && File.Exists(outFile), ...)` at line 117 cannot hold. Either the suite is red (which contradicts the handoff's "build.ps1 OK"), or the harness survives for a reason I could not read out of the code, in which case the test is quietly no longer exercising the command line it claims to. Separately, -PipeName is the one value this commit added to the relaunch and it is the one value the round-trip never checks.

**Fix:** Add `[string]$PipeName` to the driver's param() block and to the Set-Content value list and the expected array, so all four arguments are actually round-tripped, and run the suite to confirm the case.

### [medium] client/LANFax.App/TrayContext.cs:199 - Nothing checks the listener after the 10-minute window: a dead pipe server looks healthy forever

CheckPrinter only asks the printer (`_printerWatch.Observe(PrinterStatusNow())`, a registry read of the Port value) and the stuck-jobs probe is gated by `sinceStart >= StuckCheckDelay && sinceStart < StuckCheckWindow` (line 204). grep for IsListening shows the App never reads it — only PipeProbe and the tests do (client/tools/PipeProbe/Program.cs:64,156). But the listener can stop serving on its own: ServeAsync's last handler is `catch (Exception e) { Report("print pipe listener stopped: " + e.Message, e, rateLimited: false); return false; }` (PrintPipeListener.cs:339), and RunAsync treats false as "stopped" (`first = null; if (!lost) return;`), so the task ends and Start is never called again. Same for any future unexpected exception. The user gets exactly one balloon at that moment; after that the tray icon stays green, the printer reads Installed, and every job printed later queues with no listener — retried by the spooler only for an unmeasured time, then left in "Error" with no announcement because the stuck check's window closed. docs/client-spec.md:85 describes the 30 s check as port-only, so the code and the spec agree; the gap is the design.

**Fix:** In CheckPrinter, add `if (!_spool.IsListening && !_exiting) { _spool.Start(); }` and balloon when the listener was not serving (Start already re-reports a taken name), or extend StuckCheckWindow to the whole session so a queue that stops being drained is still announced.

### [low] client/LANFax.Core/PrintPipeListener.cs:203 - A concurrent Stop returns immediately, so the session-end stop can be a no-op

Stop's first act is `lock (_gate) { accept = _accept; abort = _abort; loop = _loop; _accept = null; _abort = null; _loop = null; } if (accept == null || abort == null) return;`. Two callers therefore do not both wait for the loop. ExitAppAsync runs Stop off the UI thread (`await Task.Run(_spool.Stop)`, TrayContext.cs:640) and it can take up to StopWait (7 s); if WM_ENDSESSION lands during that window, ShutdownBlocker.WndProc → SessionEnd.OnEndSession calls the same Stop, which sees nulled fields and returns at once, then `receiving()` is still true so it only logs "still arriving after the grace" (Logic/SessionEnd.cs:19-21) and WndProc returns — and Windows may end the process right there, cutting off the very job the grace exists to protect. Narrow, but it is the one path where the shutdown handling can miss.

**Fix:** Keep a generation object that every Stop caller waits on (set the fields to null but store the completion Task), so a second Stop blocks until the loop is really done, instead of returning early.

### [low] client/LANFax.Core/PrintQueueJobs.cs:22 - StuckSince also flags jobs the spooler is still retrying, and the balloon invites a restart that duplicates a fax

The filter is `(Status & JobStatusError) != 0 && (Status & (Deleting|Deleted)) == 0 && SubmittedUtc < startUtc`, and PrintQueueJobsTests deliberately counts `JobStatusError | 0x10 /* printing */` as stuck. But per docs/client-spec.md:240-244 the state of a job the spooler has *not* given up on is exactly "Error, Printing", retried for at least 4 minutes, and the probe observed delivery arriving within 30 s of the listener appearing. A job printed 10 s before LANFax starts is therefore announced as "stuck ... before LANFax started" at the 60 s check even though it was going to arrive by itself; if the user follows the balloon and restarts it, the project's own measurement (client-spec.md:252-254: one restarted job delivered twice, 0.5 s apart) means two faxes. The message warns about this rather than avoiding it, which is a deliberate choice, so I rate it low. (The Submitted handling is correct: JOB_INFO_1 documents that SYSTEMTIME as UTC, so comparing against DateTime.UtcNow is right.)

**Fix:** Either delay the first check past the observed retry interval, or skip jobs whose status still carries JOB_STATUS_PRINTING/SPOOLING (0x10/0x800) — those are the spooler's own pending retries — and keep the flag only for jobs that settled into a bare Error.

### [low] client/LANFax.Core/PrintPipeListener.cs:230 - Start reports our own lingering listener as a squatter

When a previous Stop had to give up, Start sets `stillWriting` and skips the sweep, but then still calls `CreateInstance(first: true)` while that loop holds the pipe's instances. The create fails with UnauthorizedAccessException, CannotTake maps that to NameTaken — "another program is already listening on \\.\pipe\LANFax (a second LANFax, LANFax of another signed-in user, or a program posing as it)" — and RunAsync retries every NameRetry (5 s) and re-reports every NameTakenRepeat (10 min). The message points the user at a squatter that does not exist, and Start blocks the UI thread for up to StopWait first (`_lingering?.Wait(StopWait)`, line 167), which the tray calls from its constructor.

**Fix:** Detect the lingering case (it is known: `stillWriting`) and say "the previous LANFax instance is still finishing", or keep waiting on _lingering until it exits before creating the first instance.

### [low] client/tools/PipeProbe/Program.cs:140 - The -Same probe reports FAIL for the outcome the spec records as normal

`bool differ = hashes.Count == n && distinct == n && leftSame.Count == 0; Console.WriteLine(differ ? "RESULT: PASS (every reprint differs byte-wise)" : "RESULT: FAIL (identical reprints, or missing files)"); return differ ? 0 : 1;`. docs/client-spec.md:258-262 records the measured result of this very mode as "4 prints, 3 distinct SHA-256", i.e. the probe's FAIL branch, and that measurement is the stated reason no hash dedup was built. Anyone re-running `-Same` as a regression check gets a red exit code for a property of Print To PDF, not a defect — and the mode also conflates two different things (identical bytes, which is informational, with a missing file, which is a real loss).

**Fix:** Report the distinct count and exit non-zero only when hashes.Count != n or jobs were left in the queue; print something like "NOTE: N of M reprints byte-identical (expected: CreationDate has 1 s resolution)".

### [note] docs/client-spec.md:100 - Spec says the rescued print.pdf is only offered at tray start; the code also offers it right after an install

The spec describes the migration as moving the leftover "to `spool\jobs\<guid>.pdf` (the tray offers it as unsent at its start)". The code additionally rescans after a successful install: `InstallPrinterAsync` → `OfferRescuedDocuments()` (TrayContext.cs:566, 580) → `JobTracker.NewDocuments(_offeredSpool)` (JobTracker.cs:250), with `_offeredSpool` seeded by Scan and by every NewPrintJob so nothing is offered twice. The code does more than the spec says; worth a line in "Spool path and the printer" so the next reviewer does not read NewDocuments as dead code.

**Fix:** Amend the sentence to "... offers it as unsent at its start, and again right after a printer install (OfferRescuedDocuments / JobTracker.NewDocuments)."

## Could not verify

- I could not run anything (read-only; the shell was limited to git, and even `git show ... | findstr` was denied), so no test outcome in this review is observed — only reasoned from the source. In particular finding 3 (the -PipeName round-trip test) is derived from the scripts' $argLine text plus PowerShell's -File parameter binding; I could not confirm which of the two failure modes it hits, nor whether the suite is currently red.
- Live spooler behaviour is taken from the commit's stated measurements and docs/client-spec.md "Why a pipe port" (one connection per job, separate connections in print order, retries held for 4+ min, a cut-off job not retried). Nothing here re-measures them; findings 4 and 7 depend on those characterisations being right.
- The numeric value of PipeAccessRights.ReadWrite|Synchronize|Delete (the 0x13019b the DACL and the spec both claim) comes from the assertion in PrintPipeListenerTests.cs:470, not from an evaluated expression; I could not print the enum.
- Whether PowerShell coerces $null to an empty string for a [string]-typed parameter (which decides finding 3's exact failure mode) could not be checked.
- server/ and the faxd daemon were out of scope for this request and were not reviewed.
- docs/handoff.md:88 still says "Installer: kept printed jobs removed on every run (Printed, not Error)" while round 3 of the same log and the code list them without removing them (Install-LANFaxPrinter.ps1:118-125). I read it as a stale log line rather than a defect and did not count it, but I could not check which is authoritative for the project's conventions.

## Could not reach

- `run_shell_command`: git show eeb92ef --stat --numstat | more +40 & git ls-files client/LANFax.Tests client/LANFax.App.Tests docs
- `run_shell_command`: git show c677f1a:client/install/Install-LANFaxPrinter.ps1 | findstr /n "argLine param( PipeName"

## Disposition

Fixed in the working tree after verification (not committed).

- medium PrinterSetup.cs:148 (migration warnings thrown away): fixed. The tray runs the script through an -EncodedCommand wrapper that writes its output to a temp file (UAC kept), logs every line, puts the first warning in the balloon and treats a non-zero exit as failure; the tray retries the print.pdf rescue itself (`OldPortLeftover`) at every start and after every install.
- medium PrintPipeListener.cs:453 (rename failure swept to rejected): fixed. A failed rename copies the whole job to `<id>.pdf` and raises it; a `.part` beside its saved `.pdf` is set aside as a copy with the correct log wording. First pass also finished a complete `.part` without its `.pdf` into `<id>.pdf` at the next start; the review gate confirmed that re-offers a job whose copy was already sent (the `.part` delete had failed, the tracker moved the `.pdf`). Now every stale `.part` goes to rejected\, the complete one with one Error ("may already have been faxed ... check before faxing it"); the rename-and-copy-both-failed case, which it cannot be told apart from, says so at arrival. Chosen over a `.delivered` marker: no second file whose own write or delete can fail, and a duplicate fax is worse than one manual reprint.
- medium InstallScriptTests.cs:90 (-PipeName not round-tripped): fixed (the report's "test fails" was refuted; the coverage gap was real). The driver declares -PipeName with [CmdletBinding()], a value with a space is passed and checked; dropping -PipeName from the relaunch now fails the test.
- medium TrayContext.cs:199 (no listener check after 10 min): fixed. The 30 s printer tick restarts a listener that is no longer running (`IsRunning`), with one balloon per session (`ListenerWatch`), and restarts the stuck-job window.
- low PrintPipeListener.cs:203 (concurrent Stop no-op): fixed. A second Stop waits for the same loop until the first Stop's StopWait runs out.
- gate low (TrayContext.CheckListener restarted the listener after the session-end stop): fixed. Session end and Exit call `ListenerWatch.StoppedOnPurpose`; the tick then never restarts it.
- gate low (install wrapper `exit $LASTEXITCODE` inherited a tolerated native command's code): fixed. The wrapper exits 0 unless the script threw (1) or ended with its own non-zero `exit N` (N, detected by `$?`, measured on PowerShell 5.1 and 7).
- gate low (tests not shown red): every new or changed test in this pass was seen failing on an assertion by a temporary code mutation, then restored; none needed changing.
- handoff.md stale "kept printed jobs removed on every run" line (Could not verify, last item): corrected.
- low PrintQueueJobs.cs:22 (StuckSince flags retrying jobs): not in the verified list for this pass; unchanged.
- low PrintPipeListener.cs:230 (own lingering listener reported as a squatter): fixed (latent in the app). While the loop a Stop gave up on still runs, Start and the retry loop report "the previous listener ... is still finishing a job".
- low tools/PipeProbe/Program.cs:140 (-Same reports FAIL for the normal outcome): fixed. It fails only on missing files or jobs left in the queue; identical reprints are a NOTE. Usage line in Probe-PipePrinter.ps1 updated.
- note client-spec.md:100 (post-install offer missing from the spec): fixed in docs/client-spec.md.
