package com.google.googlesignin;

public class SignInResultWrapper {
    private String authCode;

    public SignInResultWrapper(String authCode) {
        this.authCode = authCode;
    }
    public String getServerAuthCode() {
        return authCode;
    }
}
