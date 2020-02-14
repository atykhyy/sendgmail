SendGmail is a plug-in for [git-send-email](https://git-scm.com/docs/git-send-email) on Windows that enables you to send mail from your Gmail account using OAuth 2 authorization. It is a standalone .NET executable which is intended to be specified in .gitconfig as the "sendmail server" in place of Google's SMTP server. Following GCM, it stores credentials in Windows Credential Store.

Installation
------------

In order to begin using this plug-in, follow the steps below in your Google account. Note that the project, product and client ID names are purely for your own convenience; choose something descriptive, such as `git-send-email-xoauth2`.

1. Go to [Google Cloud Platform console](https://console.cloud.google.com) and create a project (or use an existing one).
2. Select your project from the Dashboard and go to 'APIs & Services' - 'Credentials'.
3. You will be prompted to fill out the OAuth consent screen. The only thing you need to enter is a product name.
4. Select 'Create Credentials' - 'OAuth client ID'. Under Application type select 'Other' and specify a name for the client ID.
5. You will need to enter the client ID and client secret when you use the plug-in for the first time.

To install SendGmail, download it to a suitable location and modify the `[sendemail]` section of your .gitconfig file as follows:

    [sendemail]
	smtpserver = "C:/full/path/to/SendGmail.exe"
	smtpuser = you@gmail.com

SendGmail will ask you to enter the client ID and client secret the next time you use git-send-email, and perform an OOB OAuth authorization sequence. Afterwards it will attempt to refresh OAuth access tokens automatically.

Rationale
---------

There are a few options for sending mail through Google's SMTP servers, but they are less than ideal from a security perspective:

* Use your Gmail username and plaintext password. This is bad in itself, and also requires you to "enable less secure apps" in your account settings.
* Create an App Password. This is not as bad as using your account password, but it still gives the app access to your whole account, and has a number of [annoying limitations](https://support.google.com/accounts/answer/185833?hl=en):
  + Can't be used if you don't have 2FA configured (ok, I know, but.)
  + Can't be used unless you enable phone-based 2FA
  + Can't be used if you use an organizational account
  + Can't be used if you have turned on Advanced Protection for your account

Using OAuth authorization allows you to circumvent these limitations, avoid storing plaintext passwords, and restrict the app's access to the one function it needs, i.e. send email on your behalf.

Acknowledgements
----------------
[Veltro](https://www.perlmonks.org/?node_id=1218405) of PerlMonks.
