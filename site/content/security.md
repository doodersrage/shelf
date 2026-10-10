---
title: Accounts and security
description: Passwords, passkeys, two-step sign-in, signed-in devices, the activity log, and what an admin can do.
---

# Accounts and security

## Signing in

Every page and every API call needs a signed-in reader. A sign-in lasts thirty days on that browser, and each one is listed on **Account** under **Signed-in devices**, with its browser and when it was last used. Sign out any of them from there, or **Sign out everywhere else**.

Changing your password signs you out everywhere. Sign-in and sign-up allow ten attempts a minute from one address (`Accounts:SignInsPerMinute`).

## Passkeys

A passkey signs you in with your device's fingerprint, face, or PIN, or a password manager, with no password to type and no code to copy. It counts as both steps of two-step sign-in.

1. On **Account**, under **Passkeys**, name it (*My phone*, say) and choose **Add a passkey**. Your device asks you to confirm.
2. Next time, choose **Sign in with a passkey** on the sign-in page.

Each passkey is listed with when it was added and last used, and **Remove** takes one away. Shelf keeps only the public half of each key.

> [!NOTE]
> Browsers make passkeys only for an `https://` address, or `localhost`. Behind a reverse proxy that hides the address readers use, set `Passkeys:Origin` to it, such as `https://shelf.example.org`.

## Two-step sign-in

Two-step sign-in asks for a code from an authenticator app after your password.

1. On **Account**, choose **Set up two-step sign-in**, and scan the code with an authenticator app, or type its key in.
2. Enter the six-digit code it shows, and choose **Turn on**.
3. Keep the ten **recovery codes** somewhere safe. Each works once, in place of a code, if you lose the authenticator.

The secret is sealed with the shelf's keys, so a copy of the database alone can't use it. To turn it off, enter a current code.

## The activity log

Shelf keeps a record of:

- sign-ups, and failed sign-ins for names that exist;
- password changes and resets;
- admin rights given or taken, and removed accounts;
- two-step sign-in turned on or off, and passkeys added or removed;
- devices signed out from the list;
- device keys and KOReader passwords made;
- restores, snapshots, and backups taken by hand, and imports from Audiobookshelf.

Every reader sees the lines about their own account under **Recent activity** on **Account**; if something there wasn't you, change your password. An admin reads the whole log on **Readers**, or as JSON at `/admin/audit`. The newest 5,000 entries are kept.

## Admins

The first account is an admin. An admin manages readers from **Readers**:

- **A new password** for a reader who forgot theirs. It signs them out everywhere, turns off their two-step sign-in, and removes their passkeys: the way back in after a lost phone, and a way to shut out anyone who added a passkey of their own.
- **Make admin** for another reader, or take it away. The shelf always keeps at least one admin.
- **Delete** a reader, with their books and files. Books lent to them go back to their owners.

Readers can delete their own account from **Account**, with their password.

### When the only admin is locked out

Reset a password, or make someone an admin, from the command line. Each prints what it did and exits:

```bash
# In Docker
docker exec shelf dotnet Shelf.Api.dll --reset-password "Their Name"
docker exec shelf dotnet Shelf.Api.dll --make-admin "Their Name"

# From source
dotnet run --project src/Shelf.Api -- --reset-password "Their Name"
```

## Closing sign-up

Once your readers have accounts, set `Accounts:AllowSignUp` to `false`. The sign-up page then says sign-up is closed, though the first account can always be made.
