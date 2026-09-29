import { Injectable } from '@angular/core';
import {
  HttpRequest,
  HttpHandler,
  HttpEvent,
  HttpInterceptor,
  HttpErrorResponse
} from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { Router } from '@angular/router';

@Injectable()
export class AuthInterceptor implements HttpInterceptor {

  constructor(
    private router: Router
  ) {}

  intercept(
    request: HttpRequest<unknown>,
    next: HttpHandler
  ): Observable<HttpEvent<unknown>> {

    const clonedRequest = request.clone({
      withCredentials: true
    });

    return next.handle(clonedRequest).pipe(
      catchError((error: HttpErrorResponse) => {

        if (error.status === 401) {

          const url = request.url.toLowerCase();

          const isLoginRequest =
            url.includes('/api/auth/login');

          const isMeRequest =
            url.includes('/api/auth/me');

          const isRegisterRequest =
            url.includes('/api/auth/register');

          if (
            !isLoginRequest &&
            !isMeRequest &&
            !isRegisterRequest
          ) {
            this.router.navigate(
              ['/login'],
              {
                queryParams: {
                  returnUrl: this.router.url
                }
              }
            );
          }
        }

        return throwError(() => error);
      })
    );
  }
}

