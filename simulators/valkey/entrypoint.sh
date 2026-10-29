#!/bin/sh
set -eu

acl_file=/tmp/valkey-users.acl
username=${VALKEY_SIM_USERNAME:-}
password=${VALKEY_SIM_PASSWORD:-}

if [ -n "$username" ] || [ -n "$password" ]; then
    if [ -z "$username" ] || [ -z "$password" ]; then
        echo "VALKEY_SIM_USERNAME and VALKEY_SIM_PASSWORD must both be set" >&2
        exit 1
    fi

    case "$username" in
        *[!A-Za-z0-9._-]*)
            echo "VALKEY_SIM_USERNAME may contain only letters, digits, dot, underscore and hyphen" >&2
            exit 1
            ;;
    esac

    umask 077
    password_hash=$(printf '%s' "$password" | sha256sum | cut -d ' ' -f 1)
    if [ "$username" = "default" ]; then
        printf 'user default on #%s ~* &* +@all\n' "$password_hash" > "$acl_file"
    else
        printf 'user default off\nuser %s on #%s ~* &* +@all\n' "$username" "$password_hash" > "$acl_file"
    fi
else
    printf 'user default on nopass ~* &* +@all\n' > "$acl_file"
fi

exec "$@" --aclfile "$acl_file"
