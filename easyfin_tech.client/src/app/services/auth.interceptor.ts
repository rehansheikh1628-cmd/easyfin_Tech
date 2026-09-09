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
  constructor(private router: Router) {}

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    const cloned = request.clone({
      withCredentials: true
    });

    return next.handle(cloned).pipe(
      catchError((error: HttpErrorResponse) => {
        if (error.status === 401) {
          const url = request.url.toLowerCase();
          if (!url.includes('/api/auth/login') && !url.includes('/api/auth/me') && !url.includes('/api/auth/register')) {
            this.router.navigate(['/login'], { queryParams: { returnUrl: this.router.url } });
          }
        }
        return throwError(() => error);
      })
    );
  }
}
