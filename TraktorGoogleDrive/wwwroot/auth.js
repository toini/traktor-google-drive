window.googleLogin = () => {
    if (!window.tokenClient) {
        window.tokenClient = google.accounts.oauth2.initTokenClient({
            client_id: '219145501841-n6pki0jbvkue0u3vusmnguld6m4fugp9.apps.googleusercontent.com',
            scope: 'https://www.googleapis.com/auth/drive.readonly',
            callback: (tokenResponse) => {
                if (tokenResponse && tokenResponse.access_token) {
                    sessionStorage.setItem('access_token', tokenResponse.access_token);
                    console.log('Access token acquired');
                } else {
                    console.error('Token response invalid:', tokenResponse);
                }
            }
        });
    }

    window.tokenClient.requestAccessToken();
};
