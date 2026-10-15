# LANFax Client

Send and receive faxes from any Windows PC on the network, through an Asterisk fax server, without any modems (all SIP/VoIP).

What you get:

- A "LAN Fax" printer. Print from any program, type the phone number, done.
- Pick files instead of printing: "Send a fax..." lets you choose several
  documents (a cover page plus PDFs) and sends them as one fax.
- See the pages before they go: the send window previews the pages (the
  first eight of a long fax) exactly as they will be faxed, header and page
  numbers included.
- Pages are checked before sending: if a page looks blank, you are asked
  before it goes out.
- Every sent fax gets a confirmation you can open from the notification or
  the list, with the time, page count and transmission log. Finished faxes
  also get a printable confirmation sheet (like a fax machine's), kept on
  the server to open, print or share whenever you need it.
- A fax that is still going out can be cancelled from the list, and numbers
  can be copied from any row.
- LANFax checks for a newer version when it starts and offers to install it (or use "Check for updates" in Settings).
- A status page you can open in a browser, including from your phone over
  the VPN: see what is sending, what arrived, arm the line to receive, and
  send a test page to a fax number with one button.

## Download

Get `LANFax-Setup.exe` from the [Releases page](https://github.com/legoj15/LANFax/releases/latest) and run it.

## Parts

- `server/` runs on the fax server and provides the status page and the
  connection the printer uses.
- `client/` is the Windows program that installs the printer and shows
  status. (Coming after the server part.)

## Build and deploy the server part

Run the tests, then deploy to the server:

```powershell
cd server
python -m unittest discover -s tests -t .
.\deploy.ps1
```

Deployment prints the address of the status page and its access token.

## More

- `docs/design.md` explains why it is built this way.
