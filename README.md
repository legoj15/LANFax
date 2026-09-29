# LANFax

Send and receive faxes from any Windows PC on the network, through the
office fax server, without a modem in the PC.

What you get:

- A "LAN Fax" printer. Print from any program, type the phone number, done.
- A status page you can open in a browser, including from your phone over
  the VPN: see what is sending, what arrived, arm the line to receive, and
  send a test page to a customer's fax number with one button.
- The old way still works: drop a PDF named with the phone number into the
  shared `fax\outbox` folder.

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
- `docs/handoff.md` tracks what is done and what is next.
